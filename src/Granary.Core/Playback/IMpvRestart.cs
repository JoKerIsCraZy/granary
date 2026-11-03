namespace Granary.Core.Playback;

/// <summary>How a start of the player's mpv for Apply now went.</summary>
public enum MpvRestartOutcome
{
    /// <summary>mpv runs again with mpv.conf as it is now.</summary>
    Started,

    /// <summary>A title plays: mpv starts again with the next title, or once the title ends or the player closes.</summary>
    Waiting,

    /// <summary>The new mpv did not start; the player plays on with the one before, and the start is still to come.</summary>
    Failed,
}

/// <summary>
/// The player's mpv for this run, started again so it reads mpv.conf anew (Settings → Playback → This PC → Apply now).
/// Playback is never cut for it: while a title plays, the start waits for the next title or for the player to close.
/// </summary>
public interface IMpvRestart
{
    /// <summary>mpv runs in this run. Before the player first opens there is nothing to start again: mpv reads the file when it starts.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts mpv again with mpv.conf as it is now, at once when nothing plays; otherwise it waits for the title that plays
    /// to end or the player to close. <see cref="Restarted"/> or <see cref="RestartFailed"/> follows once it was tried.
    /// </summary>
    MpvRestartOutcome Restart();

    /// <summary>mpv runs with mpv.conf as it is now: it started again, or for the first time. Raised on any thread.</summary>
    event EventHandler? Restarted;

    /// <summary>A start for Apply now failed, at once or once it had waited; mpv plays on as before. Raised on any thread.</summary>
    event EventHandler? RestartFailed;
}
