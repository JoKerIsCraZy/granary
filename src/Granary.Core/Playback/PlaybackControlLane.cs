using System.Net.WebSockets;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>What the player made of a command: carried out or not, and what it does once the server was told.</summary>
/// <param name="Then">Runs after the result went out: a stop leaves the player, which must not cut off its own answer.</param>
public sealed record PlaybackControlReply(bool Completed, string? Error = null, Action? Then = null)
{
    public static PlaybackControlReply Done { get; } = new(true);

    public static PlaybackControlReply Rejected(string error) => new(false, error);
}

/// <summary>
/// The playback session's control socket, as the web player and Silo's Android app keep it: a ticket for the session,
/// the socket, <c>hello</c> once it opened, and for each command an <c>ack</c>, the player's work, and a <c>result</c>.
/// A lost connection comes back after 0.5, 1, 2, 5, 10, then 30 seconds (from the start again once one held 30 seconds);
/// playback goes on meanwhile. A server that does not serve the handshake (404, 501, 503) or a session that ended has no
/// lane, and an administrator's terminate ends it for good.
/// </summary>
/// <remarks>The socket runs off the UI thread; <c>onCommand</c> is called there and hands the work to the player itself.</remarks>
public sealed class PlaybackControlLane : IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    // A connection that held this long was a good one: the next loss starts the waits over. One the server closed sooner
    // (another player took the lane over, the account changed) keeps them growing, so it is not asked for twice a second.
    private static readonly TimeSpan SteadyAfter = TimeSpan.FromSeconds(30);

    private readonly PlaybackControlApi _api;
    private readonly Func<IPlaybackControlSocket> _sockets;
    private readonly TimeProvider _time;
    private readonly ILogger<PlaybackControlLane> _logger;
    private CancellationTokenSource? _run;

    public PlaybackControlLane(PlaybackControlApi api, Func<IPlaybackControlSocket> sockets, TimeProvider time, ILogger<PlaybackControlLane> logger)
    {
        _api = api;
        _sockets = sockets;
        _time = time;
        _logger = logger;
    }

    /// <summary>The session the lane is kept for; null while it keeps none.</summary>
    public string? SessionId { get; private set; }

    /// <summary>The run that keeps the lane, for a caller that waits for it to end.</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Keeps the lane of <paramref name="sessionId"/>, ending the one of an earlier session.</summary>
    public void Follow(Uri server, string sessionId, string installationId, Func<PlaybackControlCommand, Task<PlaybackControlReply>> onCommand)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(onCommand);
        if (SessionId == sessionId && !Running.IsCompleted)
        {
            return;
        }
        Cancel();
        SessionId = sessionId;
        _run = new CancellationTokenSource();
        Running = RunAsync(server, sessionId, installationId, onCommand, _run.Token);
    }

    /// <summary>Ends the lane and waits until its connection closed. Never throws: whatever ended the run was logged.</summary>
    public async Task StopAsync()
    {
        Cancel();
        SessionId = null;
        try
        {
            await Running.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("The control lane ended with {Failure}", ex.GetType().Name);
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

    private async Task RunAsync(Uri server, string sessionId, string installationId, Func<PlaybackControlCommand, Task<PlaybackControlReply>> onCommand,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Ending ending;
            DateTimeOffset? openedAt = null;
            try
            {
                ending = await ConnectOnceAsync(server, sessionId, installationId, onCommand, () => openedAt = _time.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SiloApiException ex) when (ex.Status is 404 or 501 or 503)
            {
                // The server serves no handshake, or the session ended: there is nothing to keep.
                _logger.LogInformation("The playback session {SessionId} has no control lane ({Status})", sessionId, ex.Status);
                return;
            }
            catch (Exception ex) when (ex is SiloApiException or HttpRequestException or WebSocketException or InvalidDataException
                or System.Text.Json.JsonException or OperationCanceledException)
            {
                _logger.LogDebug("The control lane of {SessionId} closed: {Failure}", sessionId, ex.GetType().Name);
                ending = Ending.Lost;
            }
            catch (Exception ex)
            {
                // Anything else is no reason to give the lane up; only its type is logged, as a message could carry the ticket.
                _logger.LogWarning("The control lane of {SessionId} failed: {Failure}", sessionId, ex.GetType().Name);
                ending = Ending.Lost;
            }
            if (ending == Ending.Terminated)
            {
                return;
            }
            if (openedAt is { } at && _time.GetUtcNow() - at >= SteadyAfter)
            {
                attempt = 0;
            }
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

    private async Task<Ending> ConnectOnceAsync(Uri server, string sessionId, string installationId,
        Func<PlaybackControlCommand, Task<PlaybackControlReply>> onCommand, Action opened, CancellationToken cancellationToken)
    {
        var ticket = await _api.MintTicketAsync(server, sessionId, installationId, cancellationToken).ConfigureAwait(false);
        if (!ticket.IsValid)
        {
            throw new InvalidDataException("The server's control ticket is not one of this protocol.");
        }
        await using var socket = _sockets();
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connecting.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(PlaybackControlApi.SocketUri(server, sessionId),
                [PlaybackControlApi.Protocol, $"silo.ticket.{ticket.Ticket}"], connecting.Token).ConfigureAwait(false);
        }
        if (socket.SubProtocol != PlaybackControlApi.Protocol)
        {
            throw new InvalidDataException("The server chose another protocol.");
        }
        opened();
        await socket.SendAsync(PlaybackControlFrames.Hello(sessionId), cancellationToken).ConfigureAwait(false);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(ticket.MaxConnectionSeconds), _time);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (await socket.ReceiveAsync(connection.Token).ConfigureAwait(false) is { } text)
        {
            if (PlaybackControlFrames.ParseCommand(text, sessionId) is not { } command || !seen.Add(command.CommandId))
            {
                continue;
            }
            await socket.SendAsync(PlaybackControlFrames.Ack(sessionId, command.CommandId), connection.Token).ConfigureAwait(false);
            var reply = await ReplyAsync(onCommand, command, connection.Token).ConfigureAwait(false);
            await socket.SendAsync(PlaybackControlFrames.Result(sessionId, command.CommandId, reply.Completed, reply.Error), connection.Token)
                .ConfigureAwait(false);
            reply.Then?.Invoke();
            // An administrator ended the session: there is nothing to come back to.
            if (command.Name == PlaybackControlCommands.Terminate && command.IsFromAdministrator && reply.Completed)
            {
                return Ending.Terminated;
            }
        }
        return Ending.Lost;
    }

    // The player's work waits for its UI thread; a lane that ends does not wait for it (an app closing would hang on it).
    private async Task<PlaybackControlReply> ReplyAsync(Func<PlaybackControlCommand, Task<PlaybackControlReply>> onCommand, PlaybackControlCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await onCommand(command).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The player could not carry out the command {Command}", command.Name);
            return PlaybackControlReply.Rejected("command_failed");
        }
    }

    private enum Ending
    {
        Lost,
        Terminated,
    }
}
