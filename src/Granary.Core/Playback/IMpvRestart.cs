namespace Granary.Core.Playback;

/// <summary>
/// The player's mpv for this run, started again so it reads mpv.conf anew (Settings → Playback → This PC → Apply now).
/// Playback is never cut for it: while a title plays, the start waits for the next title or for the player to close.
/// </summary>
public interface IMpvRestart
{
    /// <summary>mpv runs in this run. Before the player first opens there is nothing to start again: mpv reads the file when it starts.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts mpv again with mpv.conf as it is now: at once when nothing plays (true), otherwise when the title that plays
    /// ends or the player closes (false). <see cref="Restarted"/> follows once it did.
    /// </summary>
    bool Restart();

    /// <summary>mpv runs with mpv.conf as it is now: it started again, or for the first time. Raised on any thread.</summary>
    event EventHandler? Restarted;
}
