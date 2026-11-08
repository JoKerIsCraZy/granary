using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// A row whose first child is never cut: the children after it show only while the whole row fits, and are left out
/// together when it does not. The minimized player's times, with the end clock after them while the bar has room.
/// </summary>
internal sealed partial class FittingRow : Panel
{
    // Each layout pass of the bar comes here: no allocations.
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        double height = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        double first = Children.Count > 0 ? Children[0].DesiredSize.Width : 0;
        return new Size(Math.Min(Fits(width, availableSize.Width) ? width : first, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            width += Children[i].DesiredSize.Width;
        }
        bool showsAll = Fits(width, finalSize.Width);
        double left = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (i > 0 && !showsAll)
            {
                // Left out: no room, so nothing of it shows.
                child.Arrange(default);
                continue;
            }
            double childWidth = Math.Min(child.DesiredSize.Width, Math.Max(0, finalSize.Width - left));
            child.Arrange(new Rect(left, 0, childWidth, finalSize.Height));
            left += childWidth;
        }
        return finalSize;
    }

    // Half a pixel of rounding is no reason to leave anything out.
    private static bool Fits(double width, double room) => width <= room + 0.5;
}
