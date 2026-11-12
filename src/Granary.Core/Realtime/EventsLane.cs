using System.Net.WebSockets;
using Granary.Core.Api;
using Granary.Core.Api.Events;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Realtime;

/// <summary>How the events stream stands, as the web's activity panel names it.</summary>
public enum EventsConnection
{
    /// <summary>A ticket is asked for, or the socket opens.</summary>
    Connecting,

    /// <summary>The socket is open.</summary>
    Live,

    /// <summary>The connection ended or could not be made; the next one is tried after a wait.</summary>
    Disconnected,

    /// <summary>The server sends no events (its ticket answered 404 or 501): the lane has stopped and tries no more.</summary>
    Ended,
}

/// <summary>
/// The events stream's connection, as the web and Silo's Android app keep it (docs/api-contract.md, 12): a fresh ticket per
/// connection, the socket offering the protocol and the ticket, a <c>subscribe</c> once the server said hello (to the
/// wanted channels its hello offers), and every frame after it handed on. The server ends a connection after five minutes; a lost one comes back after 1 second, then
/// 2, 4, … up to 30 (from 1 again once one subscribed and held longer than a second), and at once after an access change
/// on a connection that held that long. A server that serves no events (404, 501) has no lane until the next one is made
/// (sign-in, another profile, the next start). A frame larger than <see cref="MaxFrameBytes"/> does not end the
/// connection: it is handed on cut to its head (<see cref="EventsFrame.IsCut"/>), as is a long frame that is only a
/// cue (<see cref="EventsFrames.TakesWhole"/>), whose payload the app's sockets never keep.
/// </summary>
/// <remarks>
/// The socket runs off the UI thread; <c>onFrame</c> is called there. Once stopped, a lane stays stopped: a late
/// <see cref="Follow"/> does nothing.
/// </remarks>
public sealed class EventsLane : IAsyncDisposable
{
    /// <summary>The close code after <c>access_changed</c>.</summary>
    public const int AccessChangedStatus = 4001;

    /// <summary>
    /// The largest frame taken whole: a snapshot of 25 unread notifications fits many times over. An administrator's
    /// <c>sessions</c> frames carry up to 200 rows of about 2 KB each (some 0.4 MB) and have no upper bound, as titles
    /// have none.
    /// </summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>
    /// What is kept of a frame that is not taken whole: enough for its type, channel and event, which come first. A
    /// frame no longer than this is always taken whole.
    /// </summary>
    public const int OversizeHeadBytes = 4096;

    // What a log line names of the channels a server turned down: the server's own bounds (32 names of 64 characters).
    private const int MaxNamedChannels = 32;
    private const int MaxChannelNameLength = 64;

    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly EventsApi _api;
    private readonly Func<IRealtimeSocket> _sockets;
    private readonly TimeProvider _time;
    private readonly ILogger<EventsLane> _logger;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _run;
    private Task _running = Task.CompletedTask;
    private bool _stopped;

    public EventsLane(EventsApi api, Func<IRealtimeSocket> sockets, TimeProvider time, ILogger<EventsLane> logger)
    {
        _api = api;
        _sockets = sockets;
        _time = time;
        _logger = logger;
    }

    /// <summary>The run that keeps the lane, for a caller that waits for it to end.</summary>
    public Task Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>
    /// Keeps a connection to <paramref name="server"/> on <paramref name="channels"/>, ending an earlier one. The run starts
    /// off the caller's thread, so a caller may hold a lock of its own; after <see cref="StopAsync"/> it does nothing.
    /// <paramref name="onConnection"/> hears how the connection stands, on the lane's thread as the frames; the end of a
    /// lane that was stopped or replaced is not reported.
    /// </summary>
    public void Follow(Uri server, IReadOnlyList<string> channels, Action<EventsFrame> onFrame, Action<EventsConnection>? onConnection = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(onFrame);
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
            CancelLocked();
            var run = new CancellationTokenSource();
            _run = run;
            var listener = new Listener(onFrame, onConnection, _logger);
            _running = Task.Run(() => RunAsync(listener, server, channels, run.Token), CancellationToken.None);
        }
    }

    /// <summary>Ends the lane for good and waits until its connection closed. Never throws: whatever ended the run was logged.</summary>
    public async Task StopAsync()
    {
        Task running;
        lock (_gate)
        {
            _stopped = true;
            CancelLocked();
            running = _running;
        }
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("The events lane ended with {Failure}", ex.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // Disposed only once its run ended: the run may still be inside a call that holds its token.
    private void CancelLocked()
    {
        if (_run is not { } run)
        {
            return;
        }
        _run = null;
        run.Cancel();
        _ = _running.ContinueWith(_ => run.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunAsync(Listener listener, Uri server, IReadOnlyList<string> channels, CancellationToken cancellationToken)
    {
        var wait = FirstWait;
        // Since when the lane has had no subscribed connection: what a new one may have missed. None before the first.
        DateTimeOffset? since = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var attempt = new Attempt(since);
            listener.Connection(EventsConnection.Connecting);
            try
            {
                await ConnectOnceAsync(listener, server, channels, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SiloApiException ex) when (ex.Status is 404 or 501)
            {
                _logger.LogInformation("The server sends no events ({Status})", ex.Status);
                listener.Connection(EventsConnection.Ended);
                return;
            }
            catch (Exception ex) when (ex is SiloApiException or HttpRequestException or WebSocketException or InvalidDataException
                or System.Text.Json.JsonException or OperationCanceledException)
            {
                _logger.LogDebug("The events connection closed: {Failure}", ex.GetType().Name);
            }
            catch (Exception ex)
            {
                // No reason to give the lane up; only the type is logged, as a message could carry the ticket.
                _logger.LogWarning("The events connection failed: {Failure}", ex.GetType().Name);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            listener.Connection(EventsConnection.Disconnected);
            var now = _time.GetUtcNow();
            // A connection that held a while was a good one; one closed at once (by a server that closes every one, say)
            // keeps the waits growing, so the lane does not ask for tickets in a tight loop.
            bool held = attempt.OpenedAt is { } opened && now - opened > FirstWait;
            if (attempt.Subscribed)
            {
                since = now;
                if (held)
                {
                    wait = FirstWait;
                }
            }
            if (attempt.AccessChanged && held)
            {
                // The new access goes with the next ticket: no wait.
                continue;
            }
            try
            {
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            wait = wait * 2 < LongestWait ? wait * 2 : LongestWait;
        }
    }

    private async Task ConnectOnceAsync(Listener listener, Uri server, IReadOnlyList<string> channels, Attempt attempt,
        CancellationToken cancellationToken)
    {
        var ticket = await _api.MintTicketAsync(server, cancellationToken).ConfigureAwait(false);
        if (!ticket.IsValid)
        {
            throw new InvalidDataException("The server's events ticket is not one of this protocol.");
        }
        // A lane stopped meanwhile opens no socket.
        cancellationToken.ThrowIfCancellationRequested();
        await using var socket = _sockets();
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connecting.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(EventsApi.SocketUri(server), [EventsApi.Protocol, $"silo.ticket.{ticket.Ticket}"], connecting.Token)
                .ConfigureAwait(false);
        }
        if (socket.SubProtocol != EventsApi.Protocol)
        {
            throw new InvalidDataException("The server chose another protocol.");
        }
        attempt.OpenedAt = _time.GetUtcNow();
        listener.Connection(EventsConnection.Live);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(ticket.MaxConnectionSeconds), _time);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        int requests = 0;
        while (await socket.ReceiveAsync(connection.Token).ConfigureAwait(false) is { } text)
        {
            if (EventsFrames.Parse(text) is not { } frame)
            {
                // Never its text: a frame may carry what a profile watches.
                if (text.Length > 0)
                {
                    _logger.LogDebug("A frame of the events stream could not be read ({Length} characters)", text.Length);
                }
                continue;
            }
            switch (frame.Type)
            {
                case EventsFrameTypes.Hello:
                    await socket.SendAsync(EventsFrames.Subscribe(Offered(channels, frame.Available), $"req-{++requests}"), connection.Token)
                        .ConfigureAwait(false);
                    continue;
                case EventsFrameTypes.Subscribed:
                    attempt.Subscribed = true;
                    frame = frame with { Gap = _time.GetUtcNow() - attempt.Since };
                    if (frame.Rejected.Count > 0)
                    {
                        _logger.LogInformation("The server turned down the channels {Channels}", Named(frame.Rejected));
                    }
                    break;
                case EventsFrameTypes.AccessChanged:
                    attempt.AccessChanged = true;
                    break;
                case EventsFrameTypes.Error:
                    _logger.LogDebug("The events stream sent an error: {Code}", frame.Text("code") ?? "unknown");
                    break;
            }
            listener.Frame(frame);
        }
        // Whichever of the frame and the close code comes, the change counts once.
        if (socket.CloseStatus == AccessChangedStatus && !attempt.AccessChanged)
        {
            attempt.AccessChanged = true;
            listener.Frame(new EventsFrame(EventsFrameTypes.AccessChanged));
        }
    }

    // The hello frame says which channels the server gives this connection (an administrator's only to the acting
    // administrator): only those are asked for, so nothing comes back turned down. A hello that names none, or none of
    // the wanted ones (an older server), is no say: everything is asked for and the server's answer decides.
    private static IReadOnlyList<string> Offered(IReadOnlyList<string> wanted, IReadOnlyList<string>? available)
    {
        if (available is null)
        {
            return wanted;
        }
        string[] offered = [.. wanted.Where(available.Contains)];
        return offered.Length > 0 ? offered : wanted;
    }

    // Channel names in a log line are the server's words: only what looks like one is written out.
    private static string Named(IReadOnlyList<string> channels) =>
        string.Join(", ", channels.Take(MaxNamedChannels).Select(channel => IsChannelName(channel) ? channel : "(unnamed)"))
        + (channels.Count > MaxNamedChannels ? ", …" : "");

    private static bool IsChannelName(string channel) =>
        channel.Length is > 0 and <= MaxChannelNameLength && channel.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '_' or '-' or '.');

    // Whoever follows the lane. What the app makes of a frame or of the connection's state is no reason to lose it.
    private sealed class Listener(Action<EventsFrame> onFrame, Action<EventsConnection>? onConnection, ILogger logger)
    {
        public void Frame(EventsFrame frame)
        {
            try
            {
                onFrame(frame);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "An events frame ({Type}) could not be handled", frame.Type);
            }
        }

        public void Connection(EventsConnection connection)
        {
            try
            {
                onConnection?.Invoke(connection);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The events connection's state ({Connection}) could not be handled", connection);
            }
        }
    }

    private sealed class Attempt(DateTimeOffset? since)
    {
        /// <summary>When the last subscribed connection ended; null before the lane's first.</summary>
        public DateTimeOffset? Since { get; } = since;

        public DateTimeOffset? OpenedAt { get; set; }

        public bool Subscribed { get; set; }

        public bool AccessChanged { get; set; }
    }
}
