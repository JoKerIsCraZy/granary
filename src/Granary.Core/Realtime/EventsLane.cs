using System.Net.WebSockets;
using Granary.Core.Api;
using Granary.Core.Api.Events;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Realtime;

/// <summary>
/// The events stream's connection, as the web and Silo's Android app keep it (docs/api-contract.md, 12): a fresh ticket per
/// connection, the socket offering the protocol and the ticket, a <c>subscribe</c> once the server said hello, and every
/// frame after it handed on. The server ends a connection after five minutes; a lost one comes back after 1 second, then
/// 2, 4, … up to 30 (from 1 again once one subscribed and held longer than a second), and at once after an access change
/// on a connection that held that long. A server that serves no events (404, 501) has no lane until the next one is made
/// (sign-in, another profile, the next start).
/// </summary>
/// <remarks>
/// The socket runs off the UI thread; <c>onFrame</c> is called there. Once stopped, a lane stays stopped: a late
/// <see cref="Follow"/> does nothing.
/// </remarks>
public sealed class EventsLane : IAsyncDisposable
{
    /// <summary>The close code after <c>access_changed</c>.</summary>
    public const int AccessChangedStatus = 4001;

    /// <summary>The largest frame taken: a snapshot of 25 unread notifications fits many times over.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

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
    /// </summary>
    public void Follow(Uri server, IReadOnlyList<string> channels, Action<EventsFrame> onFrame)
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
            _running = Task.Run(() => RunAsync(server, channels, onFrame, run.Token), CancellationToken.None);
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

    private async Task RunAsync(Uri server, IReadOnlyList<string> channels, Action<EventsFrame> onFrame, CancellationToken cancellationToken)
    {
        var wait = FirstWait;
        // Since when the lane has had no subscribed connection: what a new one may have missed. None before the first.
        DateTimeOffset? since = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var attempt = new Attempt(since);
            try
            {
                await ConnectOnceAsync(server, channels, onFrame, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SiloApiException ex) when (ex.Status is 404 or 501)
            {
                _logger.LogInformation("The server sends no events ({Status})", ex.Status);
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

    private async Task ConnectOnceAsync(Uri server, IReadOnlyList<string> channels, Action<EventsFrame> onFrame, Attempt attempt,
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
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(ticket.MaxConnectionSeconds), _time);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        int requests = 0;
        while (await socket.ReceiveAsync(connection.Token).ConfigureAwait(false) is { } text)
        {
            if (EventsFrames.Parse(text) is not { } frame)
            {
                continue;
            }
            switch (frame.Type)
            {
                case EventsFrameTypes.Hello:
                    await socket.SendAsync(EventsFrames.Subscribe(channels, $"req-{++requests}"), connection.Token).ConfigureAwait(false);
                    continue;
                case EventsFrameTypes.Subscribed:
                    attempt.Subscribed = true;
                    frame = frame with { Gap = _time.GetUtcNow() - attempt.Since };
                    if (frame.Rejected.Count > 0)
                    {
                        _logger.LogInformation("The server turned down the channels {Channels}", string.Join(", ", frame.Rejected));
                    }
                    break;
                case EventsFrameTypes.AccessChanged:
                    attempt.AccessChanged = true;
                    break;
                case EventsFrameTypes.Error:
                    _logger.LogDebug("The events stream sent an error: {Code}", frame.Text("code") ?? "unknown");
                    break;
            }
            Deliver(onFrame, frame);
        }
        // Whichever of the frame and the close code comes, the change counts once.
        if (socket.CloseStatus == AccessChangedStatus && !attempt.AccessChanged)
        {
            attempt.AccessChanged = true;
            Deliver(onFrame, new EventsFrame(EventsFrameTypes.AccessChanged));
        }
    }

    // What the app makes of a frame is no reason to lose the connection.
    private void Deliver(Action<EventsFrame> onFrame, EventsFrame frame)
    {
        try
        {
            onFrame(frame);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An events frame ({Type}) could not be handled", frame.Type);
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
