namespace Granary.Core.Playback;

/// <summary>
/// Full screen for the next title, Granary's own (Silo's web player ends full screen with the player): a title that opens
/// the player opens it in full screen when the player was in full screen as it was last left, kept for this PC
/// (<see cref="IPlaybackDevicePreferences.PlayerFullScreen"/>). Leaving it changes nothing about the window itself, whose
/// full screen is the whole app's (<c>WindowModeService</c> over <see cref="ViewModels.WindowModes"/>).
/// </summary>
public sealed class PlayerFullScreen(IPlaybackDevicePreferences preferences, TimeProvider time)
{
    /// <summary>
    /// How soon after an Esc out of full screen the player must be left for that to count as leaving in full screen: the
    /// second Esc of the way out, not an Esc to watch on in the window.
    /// </summary>
    internal static readonly TimeSpan EscapeWindow = TimeSpan.FromSeconds(5);

    // When Esc took the full player out of full screen, while nothing else has happened since.
    private long? _escapedAt;

    /// <summary>
    /// A title opens the player: Play, Resume, a card's play, Continue Watching, a Watch Party, <c>--play</c>. Not the bar
    /// brought back, the mini view left or the next episode, which keep the window as it is.
    /// </summary>
    public bool OpensInFullScreen => preferences.PlayerFullScreen;

    /// <summary>
    /// Esc in the full player left full screen, the first step of its way out: leaving the player within
    /// <see cref="EscapeWindow"/> (the next Esc) still counts as leaving it in full screen.
    /// </summary>
    public void EscapedFullScreen() => _escapedAt = time.GetTimestamp();

    /// <summary>
    /// The viewer did something else after an Esc (another key or click in the player, a change of the window, the bar):
    /// the window is their choice again.
    /// </summary>
    public void ForgetEscape() => _escapedAt = null;

    /// <summary>
    /// The player was left, from the full player or the bar, as the window closes too, with the window in the viewer's full
    /// screen or not; kept for the next title only when it changed.
    /// </summary>
    public void Left(bool isFullScreen)
    {
        bool escaping = _escapedAt is { } at && time.GetElapsedTime(at) <= EscapeWindow;
        _escapedAt = null;
        bool fullScreen = isFullScreen || escaping;
        if (fullScreen != preferences.PlayerFullScreen)
        {
            // Never fails: a save that does not work is logged, and the choice holds for this run.
            _ = preferences.SetPlayerFullScreenAsync(fullScreen);
        }
    }
}
