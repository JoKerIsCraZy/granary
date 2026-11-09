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
/// Windows restores it to, which follows it to another display (<see cref="KeepMaximized"/>), or else the one it had
/// before; a minimized one (Windows parks it at -32000) changes nothing, so the place stays the last one it really had,
/// maximized or not.
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
    /// The window as it is now, at <paramref name="bounds"/>. Bounds that are no real place (empty, or parked as a minimized
    /// window's, as a window being destroyed may say) leave the last good place.
    /// </summary>
    public void Keep(WindowState state, WindowBounds bounds)
    {
        switch (state)
        {
            case WindowState.Restored when IsReal(bounds):
                Bounds = bounds;
                IsMaximized = false;
                break;
            case WindowState.Maximized:
                IsMaximized = true;
                break;
        }
    }

    /// <summary>
    /// The window maximized on the display with <paramref name="workArea"/>, at <paramref name="scale"/>.
    /// <paramref name="restoreBounds"/> is where Windows restores it to, when it says: kept while its title bar lies on that
    /// display. One elsewhere was read wrong (its coordinates turned the wrong way), and is not trusted: the last place
    /// stays, moved onto that display if it is not there, its size kept as far as the display allows.
    /// </summary>
    public void KeepMaximized(WindowBounds? restoreBounds, WindowBounds workArea, double scale)
    {
        IsMaximized = true;
        if (restoreBounds is { } restore && IsReal(restore) && restore.ShowsTitleBarOn(workArea, scale))
        {
            Bounds = restore;
        }
        else if (Bounds is { } last)
        {
            Bounds = last.OnDisplays([workArea], workArea, scale);
        }
    }

    /// <summary>The place to save, with the scale of the display its bounds lie on; none while the window has had no place of its own.</summary>
    public WindowPlace? ToPlace(double scale) => Bounds is { } bounds ? new WindowPlace(bounds, scale, IsMaximized) : null;

    private static bool IsReal(WindowBounds bounds) =>
        bounds.Width > 0 && bounds.Height > 0 && bounds.X > ParkedEdge && bounds.Y > ParkedEdge;
}
