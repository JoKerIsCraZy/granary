using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// The Calendar's week grid: its items side by side in equal columns with <see cref="Spacing"/> between them, each as high
/// as the repeater is (CSS <c>grid-template-columns: repeat(7, minmax(0, 1fr))</c>), so every day's column fills the page
/// below the week bar and scrolls on its own.
/// </summary>
internal sealed partial class WeekColumnsLayout : NonVirtualizingLayout
{
    public double Spacing { get; set; } = 10;

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        int count = context.Children.Count;
        if (count == 0 || !double.IsFinite(availableSize.Width))
        {
            return new Size(0, 0);
        }
        double width = ColumnWidth(availableSize.Width, count);
        double tallest = 0;
        foreach (var child in context.Children)
        {
            child.Measure(new Size(width, availableSize.Height));
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }
        return new Size(availableSize.Width, double.IsFinite(availableSize.Height) ? availableSize.Height : tallest);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        int count = context.Children.Count;
        if (count == 0)
        {
            return finalSize;
        }
        double width = ColumnWidth(finalSize.Width, count);
        for (int i = 0; i < count; i++)
        {
            context.Children[i].Arrange(new Rect(i * (width + Spacing), 0, width, finalSize.Height));
        }
        return finalSize;
    }

    private double ColumnWidth(double total, int count) => Math.Max(0, (total - (Spacing * (count - 1))) / count);
}
