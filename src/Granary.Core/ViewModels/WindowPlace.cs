namespace Granary.Core.ViewModels;

/// <summary>
/// Where the main window was as Granary closed, to open there at the next start (<see cref="WindowPlaceFile"/>): its place
/// and size as a window, in pixels on the desktop, the scale of the display they lay on, whether it was maximized, and
/// whether it was full screen. A maximized window's bounds are the ones it had before, which un-maximizing goes back to.
/// Granary's own: Silo's clients have no desktop window.
/// </summary>
public sealed record WindowPlace(WindowBounds Bounds, double Scale, bool IsMaximized)
{
    // Windows' scales lie a quarter or more apart, custom ones a percent or more; less is the same scale.
    private const double SameScale = 0.005;

    /// <summary>
    /// Closed in full screen (<see cref="WindowModes.ReopensInFullScreen"/>): it opens so again. The bounds and
    /// <see cref="IsMaximized"/> stay the window that leaving full screen goes back to.
    /// </summary>
    public bool IsFullScreen { get; init; }

    /// <summary>
    /// Where the window opens, before it shows. <paramref name="display"/> is the work area of the display the bounds lie on
    /// now, or of the nearest one when they lie on none, and <paramref name="displayScale"/> is its scale. On a display of
    /// another scale the window keeps its size in view pixels, as Windows resizes a window moved between displays, and is
    /// fitted onto that display when it then no longer fits. Otherwise it opens as a window comes back from full screen
    /// (<see cref="WindowBounds.OnDisplays(IReadOnlyList{WindowBounds}, WindowBounds, double)"/>): where it was while its
    /// title bar shows on one of <paramref name="workAreas"/>, else moved onto the display.
    /// </summary>
    public WindowBounds OpenOn(IReadOnlyList<WindowBounds> workAreas, WindowBounds display, double displayScale)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        var bounds = Bounds;
        if (Math.Abs(displayScale - Scale) > SameScale)
        {
            bounds = bounds with
            {
                Width = (int)Math.Round(Bounds.Width * displayScale / Scale),
                Height = (int)Math.Round(Bounds.Height * displayScale / Scale),
            };
            if (bounds.Width > display.Width || bounds.Height > display.Height)
            {
                return bounds.Within(display);
            }
        }
        return bounds.OnDisplays(workAreas, display, displayScale);
    }
}
