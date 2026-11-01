namespace Granary.Core.ViewModels;

/// <summary>A window's place and size on the desktop, in pixels.</summary>
public readonly record struct WindowBounds(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// These bounds within <paramref name="area"/>, a display's work area: moved, and made smaller if they must, so a window
    /// that comes back from full screen or the mini view is on a display, even when its own was unplugged or moved meanwhile.
    /// Bounds within the area give or take <paramref name="slack"/> pixels stay as they are: a window's invisible resize
    /// frame reaches past the display's edge when the window is flush with it.
    /// </summary>
    public WindowBounds Within(WindowBounds area, int slack = 0)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return this;
        }
        if (X >= area.X - slack && Y >= area.Y - slack && X + Width <= area.X + area.Width + slack
            && Y + Height <= area.Y + area.Height + slack)
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
}
