namespace Granary.Core.Playback;

/// <summary>
/// Full screen for the next title, Granary's own (Silo's web player ends full screen with the player): a title that opens
/// the player opens it in full screen when the player was in full screen as it was last left, kept for this PC
/// (<see cref="IPlaybackDevicePreferences.PlayerFullScreen"/>). Leaving it changes nothing about the window itself, whose
/// full screen is the whole app's (<c>WindowModeService</c> over <see cref="ViewModels.WindowModes"/>).
/// </summary>
public sealed class PlayerFullScreen(IPlaybackDevicePreferences preferences)
{
    // Esc took the full player out of full screen, and nothing else changed since: as Esc is the way out of the player,
    // leaving it now still counts as leaving it in full screen.
    private bool _isEscaping;

    /// <summary>
    /// A title opens the player: Play, Resume, a card's play, Continue Watching, a Watch Party, <c>--play</c>. Not the bar
    /// brought back, the mini view left or the next episode, which keep the window as it is.
    /// </summary>
    public bool OpensInFullScreen => preferences.PlayerFullScreen;

    /// <summary>Esc in the full player left full screen, the first step of its way out (the next Esc leaves the player).</summary>
    public void EscapedFullScreen() => _isEscaping = true;

    /// <summary>
    /// The window changed some other way after an Esc (back into full screen, the mini view) or the player became the bar:
    /// the window is the viewer's choice again.
    /// </summary>
    public void ForgetEscape() => _isEscaping = false;

    /// <summary>
    /// The player was left, from the full player or the bar, with the window in full screen or not (the mini view ended
    /// first, so it is where that went back to); kept for the next title only when it changed.
    /// </summary>
    public void Left(bool isFullScreen)
    {
        bool fullScreen = isFullScreen || _isEscaping;
        _isEscaping = false;
        if (fullScreen != preferences.PlayerFullScreen)
        {
            // Never fails: a save that does not work is logged, and the choice holds for this run.
            _ = preferences.SetPlayerFullScreenAsync(fullScreen);
        }
    }
}
