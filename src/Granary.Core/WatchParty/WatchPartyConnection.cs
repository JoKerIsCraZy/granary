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
public sealed record WatchPartyEnd(string Reason, bool Replaced = false, string? Message = null);

/// <summary>
/// One room engagement's socket, as the web keeps it (<c>useWatchTogetherRoomConnection.ts</c>): a fresh ticket for
/// each connection, the socket with <c>silo.room.v2</c>, pings for the room clock, and the server's snapshots and
/// commands passed on. The server closes a connection after five minutes; a lost one comes back after 0.5, 1, 2, then
/// every 5 seconds. A room that ended, a proof the server refuses, or this profile joining on another device ends it
/// for good (<see cref="Ended"/>).
/// </summary>
/// <remarks>Its events are raised on the socket's thread.</remarks>
public sealed class WatchPartyConnection : IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly WatchPartyApi _api;
    private readonly Func<IRealtimeSocket> _sockets;
    private readonly TimeProvider _time;
    private readonly ILogger<WatchPartyConnection> _logger;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private CancellationTokenSource? _run;
    private IRealtimeSocket? _socket;
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

    public WatchPartyConnectionState State { get; private set; }

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
        if (_socket is not { } socket || State != WatchPartyConnectionState.Connected)
        {
            return false;
        }
        await _sending.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(socket, _socket))
            {
                return false;
            }
            await socket.SendAsync(frame, _run?.Token ?? CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug("A room frame was not sent: {Failure}", ex.GetType().Name);
            return false;
        }
        finally
        {
            _sending.Release();
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
        while (!cancellationToken.IsCancellationRequested)
        {
            WatchPartyEnd? end;
            SetState(WatchPartyConnectionState.Connecting);
            try
            {
                end = await ConnectOnceAsync(server, roomId, roomToken, () => attempt = 0, cancellationToken).ConfigureAwait(false);
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
                _socket = null;
            }
            if (end is not null)
            {
                SetState(WatchPartyConnectionState.Disconnected);
                Ended?.Invoke(this, end);
                return;
            }
            SetState(WatchPartyConnectionState.Disconnected);
            var delay = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];
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

    // One connection: null when it was lost (and comes back), else why the engagement is over.
    private async Task<WatchPartyEnd?> ConnectOnceAsync(Uri server, string roomId, string roomToken, Action opened, CancellationToken cancellationToken)
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
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(ticket.MaxConnectionSeconds), _time);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        _socket = socket;
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
                        _pongs++;
                        break;
                    case WatchPartyFrame.Closed closed:
                        return new WatchPartyEnd(closed.Reason);
                    case WatchPartyFrame.Replaced replaced:
                        return new WatchPartyEnd("replaced", Replaced: true, replaced.Reason);
                    case { } frame:
                        FrameReceived?.Invoke(this, frame);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
        {
            // The connection's five minutes are up: a fresh ticket opens the next one.
        }
        finally
        {
            _socket = null;
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
        return null;
    }

    // Pings come fast until the clock has a few samples of this connection, then slowly.
    private async Task PingAsync(CancellationToken cancellationToken)
    {
        _pongs = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await SendAsync(WatchPartyFrames.Ping(_time.GetUtcNow())).ConfigureAwait(false);
            await Task.Delay(_pongs < RoomClock.FastSamples ? RoomClock.FastPing : RoomClock.SlowPing, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetState(WatchPartyConnectionState state)
    {
        if (State == state)
        {
            return;
        }
        State = state;
        StateChanged?.Invoke(this, state);
    }
}
