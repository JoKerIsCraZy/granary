using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// A diagnosis rather than a feature: a tester saw the player's time stand and its buttons do nothing while the picture played
// on and Space still paused it. At each press on the player and each pause, the time the player shows is held against the
// engine's, the moments the viewer notices it; when the two stand apart while the engine plays, the log says what the player
// holds, at every such moment (a press is rare enough), and once that the time follows again. Not while the player prepares
// a title or waits for a seek of its own: then they are apart by design. The engine's load and the player's last snapshot are
// in the line, so a player that no longer hears the engine's new load shows. No timer runs for it, so nothing ticks while a
// title plays. Nothing of the title is named.
public sealed partial class PlayerViewModel
{
    // How far apart they may be: the engine raises its position several times a second, a little throttled.
    private const double StallSeconds = 3;

    private bool _stallLogged;

    /// <summary>At a press on the player and at a pause: logs when the time it shows stands apart from the engine's while that plays.</summary>
    public void CheckFollowsEngine()
    {
        var engine = _engine.Current;
        if (HasLeft || Phase != PlayerPhase.Playing || _awaitsPlayback || PendingSeek is not null
            || engine is not { IsLoaded: true, HasStarted: true, IsPaused: false, IsBuffering: false })
        {
            return;
        }
        double offset = _playback?.TimelineOffsetSeconds ?? 0;
        double engineAt = engine.Position.TotalSeconds + offset;
        bool stands = Math.Abs(engineAt - PositionSeconds) > StallSeconds;
        if (stands)
        {
            _stallLogged = true;
            _logger.LogWarning(
                "The player's time stands while the engine plays: it shows {Shown:F1} s, the engine is at {Engine:F1} s; phase {Phase}, "
                + "can seek {CanSeek}, paused {Paused}, buffering {Buffering}; playback {Playback} ({PlaybackPhase}), plays directly "
                + "{Directly}, awaits a playback {Awaits}; engine load {EngineLoad}, snapshot {Seen} of {Latest}; plan change {PlanChange}",
                PositionSeconds, engineAt, Phase, CanSeek, IsPaused, IsBuffering, _playback?.GetType().Name ?? "none",
                _playback?.Status.Phase.ToString() ?? "none", PlaysDirectly, _awaitsPlayback, engine.LoadId, _engineState.Sequence,
                engine.Sequence, PlanChange);
        }
        else if (!stands && _stallLogged)
        {
            _stallLogged = false;
            _logger.LogInformation("The player's time follows the engine again");
        }
    }
}
