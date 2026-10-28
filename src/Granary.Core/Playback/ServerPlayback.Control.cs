using Granary.Core.Api.Playback;

namespace Granary.Core.Playback;

/// <summary>A notice the player shows over the picture for a while: a message from the server or its administrator.</summary>
/// <param name="IsWarning">Amber, for a server going away; blue otherwise, as the web's PlaybackNoticeOverlay.</param>
public sealed record PlaybackNotice(string Title, string Message, bool IsWarning = false);

// The session's control lane (PlaybackControlLane): what the server or an administrator asks of the player, carried out
// as Silo's web player does (VideoPlayer.tsx executeRealtimeCommand).
public sealed partial class ServerPlayback
{
    /// <summary>Shown when an administrator ends the session, by terminate or with 410; Silo's apps and web say the same.</summary>
    public const string AdminEndedText = "An administrator ended this playback session.";

    /// <summary>Shown when an administrator stops the session.</summary>
    public const string AdminStoppedText = "An administrator stopped this playback session.";

    private const string ServerEndedText = "The server ended this playback session.";

    public event EventHandler<PlaybackNotice>? NoticeRaised;

    // The lane follows the session that plays; a session that replaces it takes the lane over.
    private void KeepControlLane()
    {
        if (_controlLane is null || _context is not { } context || _progress is not { } progress)
        {
            return;
        }
        string sessionId = progress.SessionId;
        _controlLane.Follow(context.Server.BaseUri, sessionId, context.InstallationId, command => OnControlCommandAsync(sessionId, command));
    }

    private void StopControlLane()
    {
        if (_controlLane is { } lane)
        {
            _ = lane.StopAsync();
        }
    }

    // From the lane's thread to the UI thread, where the player's state lives.
    private Task<PlaybackControlReply> OnControlCommandAsync(string sessionId, PlaybackControlCommand command)
    {
        var reply = new TaskCompletionSource<PlaybackControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                reply.TrySetResult(_stopping || _progress?.SessionId != sessionId
                    ? PlaybackControlReply.Rejected("session_replaced")
                    : CarryOut(command));
            }
            catch (Exception ex)
            {
                reply.TrySetException(ex);
            }
        });
        return reply.Task;
    }

    private PlaybackControlReply CarryOut(PlaybackControlCommand command)
    {
        // The server takes a completed pause or seek as done: one that moved nothing is refused, not claimed.
        if (command.Name is PlaybackControlCommands.Pause or PlaybackControlCommands.Unpause or PlaybackControlCommands.PlayPause
                or PlaybackControlCommands.Seek
            && Status.Phase != ServerPlaybackPhase.Playing)
        {
            return PlaybackControlReply.Rejected("not_playing");
        }
        switch (command.Name)
        {
            case PlaybackControlCommands.Pause:
                _engine.SetPaused(true);
                return PlaybackControlReply.Done;
            case PlaybackControlCommands.Unpause:
                _engine.SetPaused(false);
                return PlaybackControlReply.Done;
            case PlaybackControlCommands.PlayPause:
                _engine.SetPaused(!_paused);
                return PlaybackControlReply.Done;
            case PlaybackControlCommands.Seek:
                if (command.Number("position", "position_seconds", "seconds") is not { } position)
                {
                    return PlaybackControlReply.Rejected("missing_seek_position");
                }
                Seek(Math.Max(0, position));
                return PlaybackControlReply.Done;
            case PlaybackControlCommands.SetVolume:
                if (command.Number("volume", "level") is not { } volume)
                {
                    return PlaybackControlReply.Rejected("missing_volume");
                }
                _engine.SetVolume(Math.Clamp(volume, 0, 1) * 100);
                return PlaybackControlReply.Done;
            case PlaybackControlCommands.DisplayMessage:
                Notify(new PlaybackNotice(command.Text("title") ?? "Playback notice", command.Text("message") ?? "A server message was received."));
                return PlaybackControlReply.Done;
            case PlaybackControlCommands.ServerRestarting:
                Notify(new PlaybackNotice(command.Text("title") ?? "Server restarting",
                    command.Text("message") ?? "Playback may end shortly while the server restarts.", IsWarning: true));
                return PlaybackControlReply.Done;
            case PlaybackControlCommands.ServerShuttingDown:
                Notify(new PlaybackNotice(command.Text("title") ?? "Server shutting down",
                    command.Text("message") ?? "Playback may end shortly while the server shuts down.", IsWarning: true));
                return PlaybackControlReply.Done;
            case PlaybackControlCommands.Stop:
            case PlaybackControlCommands.Terminate:
                // The answer goes out first: leaving ends the lane, which would cut it off.
                string message = EndedText(command);
                return PlaybackControlReply.Done with { Then = () => _dispatcher.Post(() => Fail(message, canRetry: false)) };
            default:
                return PlaybackControlReply.Rejected("unsupported");
        }
    }

    // The administrator's own words when they sent some, as Silo's apps show them.
    private static string EndedText(PlaybackControlCommand command)
    {
        if (command.Text("message") is { } message && !string.IsNullOrWhiteSpace(message))
        {
            return message;
        }
        bool terminate = command.Name == PlaybackControlCommands.Terminate;
        return !command.IsFromAdministrator ? ServerEndedText : terminate ? AdminEndedText : AdminStoppedText;
    }

    private void Notify(PlaybackNotice notice) => NoticeRaised?.Invoke(this, notice);
}
