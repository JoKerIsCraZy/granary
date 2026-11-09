namespace Granary.Core.ViewModels;

/// <summary>How the main window shows while it is a window.</summary>
public enum WindowState
{
    Restored,
    Maximized,
    Minimized,
}

/// <summary>
/// The main window's place as a window, followed through its changes: to go back to after full screen or the mini view, and
/// to open at the next start. A restored window's place and size are kept as they are. A maximized window keeps the ones
/// it had before, which un-maximizing goes back to; a minimized one (Windows parks it at -32000) changes nothing, so the
/// place stays the last one it really had, maximized or not.
/// </summary>
public sealed class WindowPlaceKeeper
{
    /// <summary>The window's place and size the last time it was restored; none before it ever was.</summary>
    public WindowBounds? Bounds { get; private set; }

    /// <summary>The window was maximized the last time it was not minimized.</summary>
    public bool IsMaximized { get; private set; }

    /// <summary>The window as it is now, at <paramref name="bounds"/>.</summary>
    public void Keep(WindowState state, WindowBounds bounds)
    {
        switch (state)
        {
            case WindowState.Restored:
                Bounds = bounds;
                IsMaximized = false;
                break;
            case WindowState.Maximized:
                IsMaximized = true;
                break;
        }
    }

    /// <summary>The place to save, with the scale of the display its bounds lie on; none while the window has had no place of its own.</summary>
    public WindowPlace? ToPlace(double scale) => Bounds is { } bounds ? new WindowPlace(bounds, scale, IsMaximized) : null;
}
