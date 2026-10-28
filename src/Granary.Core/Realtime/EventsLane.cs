using System.Net.WebSockets;
using Granary.Core.Api;
using Granary.Core.Api.Events;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Realtime;

/// <summary>
/// The events stream's connection, as the web and Silo's Android app keep it (docs/api-contract.md, 12): a fresh ticket per
/// connection, the socket offering the protocol and the ticket, a <c>subscribe</c> once the server said hello, and every
/// frame after it handed on. The server ends a connection after five minutes; a lost one comes back after 1 second, then
/// 2, 4, … up to 30 (from 1 again once one subscribed), and at once after an access change. A server that serves no
/// events (404, 501) has no lane.
/// </summary>
/// <remarks>The socket runs off the UI thread; <c>onFrame</c> is called there.</remarks>
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
    private CancellationTokenSource? _run;

    public EventsLane(EventsApi api, Func<IRealtimeSocket> sockets, TimeProvider time, ILogger<EventsLane> logger)
    {
        _api = api;
        _sockets = sockets;
        _time = time;
        _logger = logger;
    }

    /// <summary>The run that keeps the lane, for a caller that waits for it to end.</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Keeps a connection to <paramref name="server"/> on <paramref name="channels"/>, ending an earlier one.</summary>
    public void Follow(Uri server, IReadOnlyList<string> channels, Action<EventsFrame> onFrame)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(onFrame);
        Cancel();
        _run = new CancellationTokenSource();
        Running = RunAsync(server, channels, onFrame, _run.Token);
    }

    /// <summary>Ends the lane and waits until its connection closed. Never throws: whatever ended the run was logged.</summary>
    public async Task StopAsync()
    {
        Cancel();
        try
        {
            await Running.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("The events lane ended with {Failure}", ex.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // Disposed only once its run ended: the run may still be inside a call that holds its token.
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

    private async Task RunAsync(Uri server, IReadOnlyList<string> channels, Action<EventsFrame> onFrame, CancellationToken cancellationToken)
    {
        var wait = FirstWait;
        // Since when the lane has had no subscribed connection: what a new one may have missed.
        var since = _time.GetUtcNow();
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
            if (attempt.Subscribed)
            {
                wait = FirstWait;
                since = _time.GetUtcNow();
            }
            if (attempt.AccessChanged)
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

    private sealed class Attempt(DateTimeOffset since)
    {
        public DateTimeOffset Since { get; } = since;

        public bool Subscribed { get; set; }

        public bool AccessChanged { get; set; }
    }
}
