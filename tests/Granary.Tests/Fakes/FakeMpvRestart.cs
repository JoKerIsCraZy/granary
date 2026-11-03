using Granary.Core.Playback;

namespace Granary.Tests.Fakes;

/// <summary>
/// The player's mpv as Apply now sees it: running or not, a title playing or not, a start that works or fails. A restart
/// asked for while one plays waits until <see cref="TitleEnds"/>, as the engine waits for the next title or for the
/// player to close.
/// </summary>
internal sealed class FakeMpvRestart : IMpvRestart
{
    public bool IsRunning { get; set; } = true;

    public bool IsPlaying { get; set; }

    /// <summary>A new mpv does not start, as when libmpv cannot make one.</summary>
    public bool FailsToStart { get; set; }

    public int Restarts { get; private set; }

    public bool IsWaiting { get; private set; }

    /// <summary>Runs as mpv starts again, before <see cref="Restarted"/>: the engine reports how the file fared there.</summary>
    public Action? WhileStarting { get; set; }

    public event EventHandler? Restarted;

    public event EventHandler? RestartFailed;

    public MpvRestartOutcome Restart()
    {
        if (IsPlaying)
        {
            IsWaiting = true;
            return MpvRestartOutcome.Waiting;
        }
        return StartNow();
    }

    /// <summary>The title ends or the player closes: a waiting restart happens now.</summary>
    public void TitleEnds()
    {
        IsPlaying = false;
        if (IsWaiting)
        {
            StartNow();
        }
    }

    /// <summary>The player opens for the first time: mpv starts with the file as it is.</summary>
    public void Starts()
    {
        IsRunning = true;
        Restarted?.Invoke(this, EventArgs.Empty);
    }

    private MpvRestartOutcome StartNow()
    {
        if (FailsToStart)
        {
            // The start stays to be done, as the engine keeps it for the next load.
            RestartFailed?.Invoke(this, EventArgs.Empty);
            return MpvRestartOutcome.Failed;
        }
        IsWaiting = false;
        Restarts++;
        WhileStarting?.Invoke();
        Restarted?.Invoke(this, EventArgs.Empty);
        return MpvRestartOutcome.Started;
    }
}
