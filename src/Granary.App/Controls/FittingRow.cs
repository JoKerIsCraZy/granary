using Granary.Core.ViewModels.Player;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// A row that leaves its later children out before it cuts its first: they show while the whole row fits
/// (<see cref="RowFit"/>), and go together when it does not. The first is cut only when it alone is wider than the row.
/// The minimized player's times, with the end time after them while the bar has room.
/// </summary>
internal sealed partial class FittingRow : Panel
{
    // Whether the later children showed at the last arrange, so they come back only with room to spare.
    private bool _showsAll = true;

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
        double wanted = RowFit.ShowsAll(width, availableSize.Width, _showsAll) ? width : first;
        return new Size(Math.Min(wanted, availableSize.Width), height);
    }

    // The arrange decides: its width is the one the row gets.
    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            width += Children[i].DesiredSize.Width;
        }
        _showsAll = RowFit.ShowsAll(width, finalSize.Width, _showsAll);
        double left = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (i > 0 && !_showsAll)
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
}
