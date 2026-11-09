namespace Granary.Core.ViewModels;

/// <summary>A window's place and size on the desktop, in pixels.</summary>
public readonly record struct WindowBounds(int X, int Y, int Width, int Height)
{
    /// <summary>The main window's title bar's height in view pixels: MainWindow's top row.</summary>
    public const double TitleBarHeight = 32;

    /// <summary>How much of the title bar has to show on a display for the window to stay where it was, in view pixels.</summary>
    public const double MinimumVisibleTitleBar = 120;

    /// <summary>
    /// <see cref="OnDisplays(IReadOnlyList{WindowBounds}, WindowBounds, int, int)"/> for the main window's title bar, on
    /// displays at <paramref name="scale"/>.
    /// </summary>
    public WindowBounds OnDisplays(IReadOnlyList<WindowBounds> workAreas, WindowBounds nearest, double scale) =>
        OnDisplays(workAreas, nearest, (int)Math.Round(TitleBarHeight * scale), (int)Math.Round(MinimumVisibleTitleBar * scale));

    /// <summary>
    /// Where a window that comes back from full screen or the mini view goes: where it was, as long as its title bar can be
    /// reached on one of <paramref name="workAreas"/> (the displays' work areas as they are now), so a window spanning two
    /// displays, or on one left of or above the primary, keeps its place and size. Only a window whose title bar shows on
    /// none, its display unplugged or moved meanwhile, is moved onto <paramref name="nearest"/>, and made smaller only if it
    /// does not fit there.
    /// </summary>
    /// <param name="titleBarHeight">The title bar's height in pixels: the top strip that has to show.</param>
    /// <param name="minimumVisibleWidth">How much of that strip has to show on one display to take hold of it, in pixels.</param>
    public WindowBounds OnDisplays(IReadOnlyList<WindowBounds> workAreas, WindowBounds nearest, int titleBarHeight, int minimumVisibleWidth)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        var titleBar = this with { Height = titleBarHeight };
        foreach (var area in workAreas)
        {
            var shown = titleBar.Overlap(area);
            if (shown.Width >= minimumVisibleWidth && shown.Height * 2 >= titleBarHeight)
            {
                return this;
            }
        }
        return Within(nearest);
    }

    /// <summary>
    /// These bounds within <paramref name="area"/>, a display's work area: moved, and made smaller if they must, to lie wholly
    /// on it.
    /// </summary>
    public WindowBounds Within(WindowBounds area)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return this;
        }
        int width = Math.Min(Width, area.Width);
        int height = Math.Min(Height, area.Height);
        return new WindowBounds(
            Math.Clamp(X, area.X, area.X + area.Width - width),
            Math.Clamp(Y, area.Y, area.Y + area.Height - height),
            width,
            height);
    }

    // The part of these bounds that lies on other; empty (zero wide or high) when none does.
    private WindowBounds Overlap(WindowBounds other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(X + Width, other.X + other.Width);
        int bottom = Math.Min(Y + Height, other.Y + other.Height);
        return new WindowBounds(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
