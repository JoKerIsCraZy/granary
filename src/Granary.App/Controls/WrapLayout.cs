using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>An <see cref="ItemsRepeater"/> layout that lines its items up and starts a new line when one is full, as CSS flex-wrap.</summary>
public sealed partial class WrapLayout : NonVirtualizingLayout
{
    public double HorizontalSpacing { get; set; } = 6;

    public double VerticalSpacing { get; set; } = 6;

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        foreach (var child in context.Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }
        return Place(context, availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize) =>
        Place(context, finalSize.Width, arrange: true);

    private Size Place(NonVirtualizingLayoutContext context, double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, used = 0;
        foreach (var child in context.Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }
            if (arrange)
            {
                child.Arrange(new Rect(x, y, size.Width, size.Height));
            }
            used = Math.Max(used, x + size.Width);
            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return new Size(used, y + lineHeight);
    }
}
