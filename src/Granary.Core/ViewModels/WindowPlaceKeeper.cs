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
/// to open at the next start. A restored window's place and size are kept as they are. A maximized window keeps the place
/// Windows restores it to, which follows it to another display, or else the one it had before; a minimized one (Windows
/// parks it at -32000) changes nothing, so the place stays the last one it really had, maximized or not.
/// </summary>
public sealed class WindowPlaceKeeper
{
    // Windows parks a minimized window at -32000; no display lies that far out.
    private const int ParkedEdge = -30000;

    /// <summary>The window's place and size the last time it was restored, or its restore place while maximized; none before it had one.</summary>
    public WindowBounds? Bounds { get; private set; }

    /// <summary>The window was maximized the last time it was not minimized.</summary>
    public bool IsMaximized { get; private set; }

    /// <summary>
    /// The window as it is now, at <paramref name="bounds"/>; <paramref name="restoreBounds"/> is where Windows restores a
    /// maximized one to, when it says. Bounds that are no real place (empty, or parked as a minimized window's, as a window
    /// being destroyed may say) leave the last good place.
    /// </summary>
    public void Keep(WindowState state, WindowBounds bounds, WindowBounds? restoreBounds = null)
    {
        switch (state)
        {
            case WindowState.Restored when IsReal(bounds):
                Bounds = bounds;
                IsMaximized = false;
                break;
            case WindowState.Maximized:
                if (restoreBounds is { } restore && IsReal(restore))
                {
                    Bounds = restore;
                }
                IsMaximized = true;
                break;
        }
    }

    /// <summary>The place to save, with the scale of the display its bounds lie on; none while the window has had no place of its own.</summary>
    public WindowPlace? ToPlace(double scale) => Bounds is { } bounds ? new WindowPlace(bounds, scale, IsMaximized) : null;

    private static bool IsReal(WindowBounds bounds) =>
        bounds.Width > 0 && bounds.Height > 0 && bounds.X > ParkedEdge && bounds.Y > ParkedEdge;
}
