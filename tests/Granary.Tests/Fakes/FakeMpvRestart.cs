using Granary.Core.Playback;

namespace Granary.Tests.Fakes;

/// <summary>
/// The player's mpv as Apply now sees it: running or not, a title playing or not. A restart asked for while one plays
/// waits until <see cref="TitleEnds"/>, as the engine waits for the next title or for the player to close.
/// </summary>
internal sealed class FakeMpvRestart : IMpvRestart
{
    public bool IsRunning { get; set; } = true;

    public bool IsPlaying { get; set; }

    public int Restarts { get; private set; }

    public bool IsWaiting { get; private set; }

    /// <summary>Runs as mpv starts again, before <see cref="Restarted"/>: the engine reports how the file fared there.</summary>
    public Action? WhileStarting { get; set; }

    public event EventHandler? Restarted;

    public bool Restart()
    {
        if (IsPlaying)
        {
            IsWaiting = true;
            return false;
        }
        Restarts++;
        WhileStarting?.Invoke();
        Restarted?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The title ends or the player closes: a waiting restart happens now.</summary>
    public void TitleEnds()
    {
        IsPlaying = false;
        if (IsWaiting)
        {
            IsWaiting = false;
            Restart();
        }
    }

    /// <summary>The player opens for the first time: mpv starts with the file as it is.</summary>
    public void Starts()
    {
        IsRunning = true;
        Restarted?.Invoke(this, EventArgs.Empty);
    }
}
