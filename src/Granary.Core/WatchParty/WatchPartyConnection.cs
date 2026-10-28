using System.Net.WebSockets;
using Granary.Core.Api;
using Granary.Core.Api.WatchParty;
using Granary.Core.Realtime;
using Microsoft.Extensions.Logging;

namespace Granary.Core.WatchParty;

public enum WatchPartyConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>Why a room engagement is over: the room's reason (<c>host_left</c>, <c>ended</c>, …), or another device took it over.</summary>
/// <param name="Message">The server's words for a takeover.</param>
public sealed record WatchPartyEnd(string Reason, bool Replaced = false, string? Message = null)
{
    /// <summary>The reason as a log may carry it: a short code, or <c>other</c> for anything the server put there besides.</summary>
    public string LoggedReason => Reason.Length is > 0 and <= 40 && Reason.All(c => char.IsAsciiLetterLower(c) || c == '_') ? Reason : "other";
}

/// <summary>
/// One room engagement's socket, as the web keeps it (<c>useWatchTogetherRoomConnection.ts</c>): a fresh ticket for
/// each connection, the socket with <c>silo.room.v2</c>, pings for the room clock, and the server's snapshots and
/// commands passed on. A connection lasts five minutes, and one that ends then (either side closing it, up to
/// <see cref="RenewMargin"/> early) is followed at once by the next; a lost one comes back after 0.5, 1, 2, then every 5
/// seconds (a little more or less, so clients do not come back together), starting over only once a connection held
/// <see cref="SteadyAfter"/>. A room that ended, a proof the server refuses, or this profile joining on another device
/// ends it for good (<see cref="Ended"/>).
/// </summary>
/// <remarks>Its events are raised on the socket's thread.</remarks>
public sealed class WatchPartyConnection : IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A connection that ends this close to its five minutes, the server's close or this side's, is renewed.</summary>
    public static readonly TimeSpan RenewMargin = TimeSpan.FromSeconds(5);

    /// <summary>A connection that held this long was a good one: the next loss starts the waits over.</summary>
    public static readonly TimeSpan SteadyAfter = TimeSpan.FromSeconds(10);

    private readonly WatchPartyApi _api;
    private readonly Func<IRealtimeSocket> _sockets;
    private readonly TimeProvider _time;
    private readonly ILogger<WatchPartyConnection> _logger;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private CancellationTokenSource? _run;
    // The open connection's socket and token, written on the socket's thread and read by senders on others as one.
    private volatile Live? _live;
    private volatile WatchPartyConnectionState _state;
    private long _epoch;
    private int _pongs;

    public WatchPartyConnection(WatchPartyApi api, Func<IRealtimeSocket> sockets, TimeProvider time, ILogger<WatchPartyConnection> logger)
    {
        _api = api;
        _sockets = sockets;
        _time = time;
        _logger = logger;
    }

    /// <summary>The server's clock as the pings measured it.</summary>
    public RoomClock Clock { get; } = new();

    /// <summary>Which connection is open: each new one counts up, and a command belongs to the one that delivered it.</summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    public WatchPartyConnectionState State => _state;

    public Task Running { get; private set; } = Task.CompletedTask;

    public event EventHandler<WatchPartyConnectionState>? StateChanged;

    /// <summary>A frame of the connection with <see cref="Epoch"/>: a snapshot or a command.</summary>
    public event EventHandler<WatchPartyFrame>? FrameReceived;

    /// <summary>The engagement is over for good: the room ended or refused this proof, or this profile joined on another device.</summary>
    public event EventHandler<WatchPartyEnd>? Ended;

    /// <summary>Keeps the socket of <paramref name="roomId"/> open, with this profile's proof for it.</summary>
    public void Open(Uri server, string roomId, string roomToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomToken);
        Cancel();
        _run = new CancellationTokenSource();
        Running = RunAsync(server, roomId, roomToken, _run.Token);
    }

    /// <summary>Sends <paramref name="frame"/> on the open connection; false when none is open or it failed.</summary>
    public async Task<bool> SendAsync(string frame)
    {
        // None while a connection is renewed or lost: the caller hears false.
        if (_live is not { } live || State != WatchPartyConnectionState.Connected)
        {
            return false;
        }
        // The connection's own token: a frame for a connection that closed is not sent on the next.
        bool entered = false;
        try
        {
            await _sending.WaitAsync(live.Token).ConfigureAwait(false);
            entered = true;
            if (!ReferenceEquals(live, _live))
            {
                return false;
            }
            await live.Socket.SendAsync(frame, live.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug("A room frame was not sent: {Failure}", ex.GetType().Name);
            return false;
        }
        finally
        {
            if (entered)
            {
                ReleaseSending();
            }
        }
    }

    /// <summary>Leaves the room: the connection closes and does not come back. Never throws.</summary>
    public async Task CloseAsync()
    {
        Cancel();
        try
        {
            await Running.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("The room connection ended with {Failure}", ex.GetType().Name);
        }
        SetState(WatchPartyConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _sending.Dispose();
    }

    // A send that finished after the dispose has nothing to release.
    private void ReleaseSending()
    {
        try
        {
            _sending.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile.
        }
    }

    private void Cancel()
    {
        if (_run is not { } run)
        {
            return;
        }
        _run = null;
        run.Cancel();
        _ = Running.ContinueWith(_ => run.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunAsync(Uri server, string roomId, string roomToken, CancellationToken cancellationToken)
    {
        int attempt = 0;
        bool renewing = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            WatchPartyEnd? end;
            DateTimeOffset? openedAt = null;
            // A renewal keeps the room connected for the moment it takes: controls are not "reconnecting" every five minutes.
            if (!renewing)
            {
                SetState(WatchPartyConnectionState.Connecting);
            }
            renewing = false;
            try
            {
                (end, renewing) = await ConnectOnceAsync(server, roomId, roomToken, () => openedAt = _time.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SiloApiException ex) when (ex.Status is 401 or 403 or 404 or 409 or 422)
            {
                // The ticket was refused for good: the room ended or this proof is no longer its.
                end = new WatchPartyEnd(ex.Status switch { 409 => "ended", 404 => "not_found", _ => "forbidden" });
            }
            catch (Exception ex) when (ex is SiloApiException or HttpRequestException or WebSocketException or InvalidDataException
                or OperationCanceledException)
            {
                // Only the type: a message could carry the ticket.
                _logger.LogDebug("The room connection closed: {Failure}", ex.GetType().Name);
                end = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("The room connection failed: {Failure}", ex.GetType().Name);
                end = null;
            }
            finally
            {
                _live = null;
            }
            if (end is not null)
            {
                SetState(WatchPartyConnectionState.Disconnected);
                Ended?.Invoke(this, end);
                return;
            }
            if (openedAt is { } at && _time.GetUtcNow() - at >= SteadyAfter)
            {
                attempt = 0;
            }
            if (renewing)
            {
                // The five minutes are up: the next connection opens at once.
                continue;
            }
            SetState(WatchPartyConnectionState.Disconnected);
            var delay = Jittered(ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)]);
            attempt++;
            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // One connection: why the engagement is over, else whether its five minutes ran out (a renewal) or it was lost.
    private async Task<(WatchPartyEnd? End, bool Renew)> ConnectOnceAsync(Uri server, string roomId, string roomToken, Action opened,
        CancellationToken cancellationToken)
    {
        var ticket = await _api.MintTicketAsync(server, roomId, roomToken, cancellationToken).ConfigureAwait(false);
        if (!ticket.IsValid)
        {
            throw new InvalidDataException("The server's room ticket is not one of this protocol.");
        }
        await using var socket = _sockets();
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connecting.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(WatchPartyApi.SocketUri(server, roomId), [WatchPartyApi.Protocol, $"silo.ticket.{ticket.Ticket}"], connecting.Token)
                .ConfigureAwait(false);
        }
        if (socket.SubProtocol != WatchPartyApi.Protocol)
        {
            throw new InvalidDataException("The server chose another protocol.");
        }
        var maxConnection = TimeSpan.FromSeconds(ticket.MaxConnectionSeconds);
        using var lifetime = new CancellationTokenSource(maxConnection, _time);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var openedAt = _time.GetUtcNow();
        // The five minutes are up, by this side's clock or the server's close just before it.
        bool Renews() => !cancellationToken.IsCancellationRequested
            && (lifetime.IsCancellationRequested || _time.GetUtcNow() - openedAt >= maxConnection - RenewMargin);
        _live = new Live(socket, connection.Token);
        Interlocked.Increment(ref _epoch);
        opened();
        SetState(WatchPartyConnectionState.Connected);
        var pings = PingAsync(connection.Token);
        try
        {
            while (await socket.ReceiveAsync(connection.Token).ConfigureAwait(false) is { } text)
            {
                switch (WatchPartyFrames.Parse(text))
                {
                    case WatchPartyFrame.Pong pong:
                        Clock.Record(pong.ClientSentAt, pong.ServerReceivedAt, pong.ServerSentAt, _time.GetUtcNow());
                        Interlocked.Increment(ref _pongs);
                        break;
                    case WatchPartyFrame.Closed closed:
                        return (new WatchPartyEnd(closed.Reason), false);
                    case WatchPartyFrame.Replaced replaced:
                        return (new WatchPartyEnd("replaced", Replaced: true, replaced.Reason), false);
                    case { } frame:
                        FrameReceived?.Invoke(this, frame);
                        break;
                }
            }
        }
        catch (Exception ex) when ((ex is OperationCanceledException or WebSocketException) && Renews())
        {
            // A socket cut off as its five minutes end (one that closes cleanly answers null instead).
        }
        finally
        {
            _live = null;
            await connection.CancelAsync().ConfigureAwait(false);
            try
            {
                await pings.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The pings end with the connection.
            }
        }
        // Its five minutes up, a fresh ticket opens the next at once; anything sooner was a loss.
        return (null, Renews());
    }

    // 80 % to 120 % of the wait.
    private static TimeSpan Jittered(TimeSpan delay) => delay * (0.8 + (0.4 * Random.Shared.NextDouble()));

    // Pings come fast until the clock has a few samples of this connection, then slowly.
    private async Task PingAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _pongs, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            await SendAsync(WatchPartyFrames.Ping(_time.GetUtcNow())).ConfigureAwait(false);
            await Task.Delay(Volatile.Read(ref _pongs) < RoomClock.FastSamples ? RoomClock.FastPing : RoomClock.SlowPing, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetState(WatchPartyConnectionState state)
    {
        if (State == state)
        {
            return;
        }
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private sealed record Live(IRealtimeSocket Socket, CancellationToken Token);
}
