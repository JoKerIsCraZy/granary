using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// A row of children of its own that starts a new line when one is full, each child centred in its line, as CSS flex-wrap
/// with <c>align-items: center</c>. <see cref="WrapLayout"/> does the same for an <see cref="ItemsRepeater"/>'s items; this
/// is for a text with something after it, such as a title's facts with its quality badges.
/// </summary>
internal sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;

    public double VerticalSpacing { get; set; } = 8;

    // Each child may take the whole width, so a long text wraps by itself before anything is cut.
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }
        return Place(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Place(double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, used = 0;
        int lineStart = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                ArrangeLine(lineStart, i, y, lineHeight, arrange);
                lineStart = i;
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }
            used = Math.Max(used, x + size.Width);
            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        ArrangeLine(lineStart, Children.Count, y, lineHeight, arrange);
        return new Size(used, y + lineHeight);
    }

    // The children from start up to end stand on one line: its height is known only once the last of them is.
    private void ArrangeLine(int start, int end, double y, double lineHeight, bool arrange)
    {
        if (!arrange)
        {
            return;
        }
        double x = 0;
        for (int i = start; i < end; i++)
        {
            var child = Children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            var size = child.DesiredSize;
            child.Arrange(new Rect(x, y + ((lineHeight - size.Height) / 2), size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
        }
    }
}
