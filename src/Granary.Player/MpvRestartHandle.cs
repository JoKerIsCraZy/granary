using Granary.Core.Playback;

namespace Granary.Player;

/// <summary>
/// <see cref="IMpvRestart"/> for the app's one engine, which is made only when the player first opens: Settings can ask
/// for Apply now without making it. Until then there is nothing to start again, and the engine reads mpv.conf when it is made.
/// </summary>
internal sealed class MpvRestartHandle : IMpvRestart
{
    private volatile MpvPlaybackEngine? _engine;

    public bool IsRunning => _engine is not null;

    public event EventHandler? Restarted;

    public event EventHandler? RestartFailed;

    // Before the engine is made, the file is read when it is: nothing is to start again.
    public MpvRestartOutcome Restart() => _engine?.RestartWithConfig() ?? MpvRestartOutcome.Started;

    /// <summary>The engine was made: it runs with mpv.conf as it is now, and says when it starts again.</summary>
    public void Attach(MpvPlaybackEngine engine)
    {
        // Both live for the run.
        engine.ConfigRestarted += (_, e) => Restarted?.Invoke(this, e);
        engine.ConfigRestartFailed += (_, e) => RestartFailed?.Invoke(this, e);
        _engine = engine;
        Restarted?.Invoke(this, EventArgs.Empty);
    }
}
