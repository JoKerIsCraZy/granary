namespace Granary.Core.ViewModels;

/// <summary>How the window shows: as a window (maximized or not), full screen, or the player's mini view.</summary>
public enum WindowMode
{
    Window,
    FullScreen,
    MiniView,
}

/// <summary>
/// The window's one full-screen state, shared by the app (the title bar's button, F11), the player (F, its button, the
/// mini view) and a trailer's own full screen (docs/ui-design.md, "Full screen"): which mode the window goes to when one
/// of them asks. The App's <c>WindowModeService</c> puts it on the window. Each start begins as a window, or in full screen
/// when Granary closed in it (<see cref="ReopensInFullScreen"/>).
/// </summary>
public sealed class WindowModes
{
    // Leaving the mini view goes back to full screen: it was opened from there.
    private bool _miniViewFromFullScreen;

    // The window is full screen for a trailer alone, so it goes back to the window as the trailer leaves full screen.
    private bool _fullScreenForTrailer;

    /// <summary>What the window shows now.</summary>
    public WindowMode Mode { get; private set; }

    public bool IsFullScreen => Mode == WindowMode.FullScreen;

    public bool IsMiniView => Mode == WindowMode.MiniView;

    /// <summary>A trailer's page fills the screen; the window is full screen meanwhile.</summary>
    public bool IsTrailerFullScreen { get; private set; }

    /// <summary>Full screen, or back to the window; from the mini view straight to full screen.</summary>
    public void ToggleFullScreen() => SetFullScreen(!IsFullScreen);

    /// <summary>The viewer's full screen, which stays until they leave it, whatever plays or closes meanwhile.</summary>
    public void SetFullScreen(bool fullScreen)
    {
        if (IsMiniView && !fullScreen)
        {
            // The mini view stays; after it the window comes back as a window.
            _miniViewFromFullScreen = false;
            return;
        }
        // The viewer's own choice: it no longer ends with a trailer's.
        _fullScreenForTrailer = false;
        GoTo(fullScreen ? WindowMode.FullScreen : WindowMode.Window);
    }

    /// <summary>The player's mini view; leaving it returns to full screen or to the window, wherever it was opened from.</summary>
    public void SetMiniView(bool miniView)
    {
        if (miniView == IsMiniView)
        {
            return;
        }
        if (miniView)
        {
            _miniViewFromFullScreen = IsFullScreen;
            GoTo(WindowMode.MiniView);
            return;
        }
        GoTo(_miniViewFromFullScreen ? WindowMode.FullScreen : WindowMode.Window);
    }

    /// <summary>
    /// A trailer's page fills the screen, or no longer does. In full screen already, the window stays as it is either way;
    /// from a window it goes full screen with the trailer and back with it.
    /// </summary>
    public void SetTrailerFullScreen(bool fullScreen)
    {
        if (fullScreen == IsTrailerFullScreen)
        {
            return;
        }
        if (fullScreen)
        {
            bool wasFullScreen = IsFullScreen;
            GoTo(WindowMode.FullScreen);
            IsTrailerFullScreen = true;
            _fullScreenForTrailer = !wasFullScreen;
            return;
        }
        IsTrailerFullScreen = false;
        if (_fullScreenForTrailer)
        {
            GoTo(WindowMode.Window);
        }
    }

    /// <summary>
    /// Whether the next start opens in full screen, were Granary to close now: in the viewer's own full screen, not in a
    /// trailer's that the window would leave with it, nor in the mini view or with the window minimized, which open as a
    /// window.
    /// </summary>
    public bool ReopensInFullScreen(bool isMinimized) => IsFullScreen && !_fullScreenForTrailer && !isMinimized;

    /// <summary>The window changed by itself, not through these: the state follows it.</summary>
    public void Follow(WindowMode mode)
    {
        if (mode != Mode)
        {
            GoTo(mode);
        }
    }

    private void GoTo(WindowMode mode)
    {
        Mode = mode;
        if (mode != WindowMode.FullScreen)
        {
            // Out of full screen, a trailer's ends with it.
            IsTrailerFullScreen = false;
            _fullScreenForTrailer = false;
        }
        if (mode != WindowMode.MiniView)
        {
            _miniViewFromFullScreen = false;
        }
    }
}
