using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// Lays its children out in equal columns, as many as fit at <see cref="MinColumnWidth"/> up to <see cref="MaxColumns"/>,
/// row by row; the children of a row share the row's height, as the web dashboard's grid cells do. With
/// <see cref="EvenRows"/> it takes fewer columns rather than leave a short last row (six tiles as 3 + 3, not 4 + 2).
/// </summary>
internal sealed partial class ColumnsPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(ColumnsPanel), new PropertyMetadata(200d, OnLayoutChanged));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ColumnsPanel), new PropertyMetadata(12, OnLayoutChanged));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ColumnsPanel), new PropertyMetadata(16d, OnLayoutChanged));

    public static readonly DependencyProperty EvenRowsProperty = DependencyProperty.Register(
        nameof(EvenRows), typeof(bool), typeof(ColumnsPanel), new PropertyMetadata(false, OnLayoutChanged));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public bool EvenRows
    {
        get => (bool)GetValue(EvenRowsProperty);
        set => SetValue(EvenRowsProperty, value);
    }

    private List<UIElement> Shown => [.. Children.Where(child => child.Visibility == Visibility.Visible)];

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Shown;
        double width = double.IsInfinity(availableSize.Width) ? MinColumnWidth * Math.Min(MaxColumns, Math.Max(1, shown.Count)) : availableSize.Width;
        int columns = ColumnsFor(width, shown.Count);
        double columnWidth = ColumnWidth(width, columns);
        double height = 0;
        foreach (var row in shown.Chunk(columns))
        {
            double rowHeight = 0;
            foreach (var child in row)
            {
                child.Measure(new Size(columnWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += rowHeight + Spacing;
        }
        return new Size(width, Math.Max(0, height - Spacing));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = Shown;
        int columns = ColumnsFor(finalSize.Width, shown.Count);
        double columnWidth = ColumnWidth(finalSize.Width, columns);
        double y = 0;
        foreach (var row in shown.Chunk(columns))
        {
            double rowHeight = row.Max(child => child.DesiredSize.Height);
            for (int i = 0; i < row.Length; i++)
            {
                row[i].Arrange(new Rect(i * (columnWidth + Spacing), y, columnWidth, rowHeight));
            }
            y += rowHeight + Spacing;
        }
        return finalSize;
    }

    private int ColumnsFor(double width, int count)
    {
        int fit = (int)Math.Floor((width + Spacing) / (Math.Max(1, MinColumnWidth) + Spacing));
        int columns = Math.Clamp(fit, 1, Math.Max(1, Math.Min(MaxColumns, count)));
        while (EvenRows && columns > 1 && count % columns != 0)
        {
            columns--;
        }
        return columns;
    }

    private double ColumnWidth(double width, int columns) => Math.Max(0, (width - Spacing * (columns - 1)) / columns);

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ColumnsPanel)d).InvalidateMeasure();
}
