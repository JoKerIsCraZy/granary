using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// One edge of a card's badges, as the web's <c>flex justify-between</c> row: its first child in the left corner, its
/// second in the right, each as wide as it needs. Only when the two don't fit beside each other do both give up width,
/// in proportion to their own, and their labels end in an ellipsis.
/// </summary>
public sealed class BadgeEdge : Panel
{
    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(BadgeEdge), new PropertyMetadata(8.0, OnGapChanged));

    private double _leftWidth;
    private double _rightWidth;

    /// <summary>The least space between the two corners.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (left, right) = Corners();
        double width = availableSize.Width;
        var unbounded = new Size(width, double.PositiveInfinity);
        left?.Measure(unbounded);
        right?.Measure(unbounded);
        _leftWidth = left?.DesiredSize.Width ?? 0;
        _rightWidth = right?.DesiredSize.Width ?? 0;

        double room = Math.Max(0, width - Gap);
        double wanted = _leftWidth + _rightWidth;
        if (double.IsFinite(width) && wanted > room && wanted > 0)
        {
            double over = wanted - room;
            _leftWidth -= over * _leftWidth / wanted;
            _rightWidth -= over * _rightWidth / wanted;
            left?.Measure(new Size(_leftWidth, double.PositiveInfinity));
            right?.Measure(new Size(_rightWidth, double.PositiveInfinity));
        }
        double height = Math.Max(left?.DesiredSize.Height ?? 0, right?.DesiredSize.Height ?? 0);
        return new Size(double.IsFinite(width) ? width : _leftWidth + Gap + _rightWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (left, right) = Corners();
        left?.Arrange(new Rect(0, 0, _leftWidth, finalSize.Height));
        right?.Arrange(new Rect(Math.Max(0, finalSize.Width - _rightWidth), 0, _rightWidth, finalSize.Height));
        return finalSize;
    }

    private (UIElement? Left, UIElement? Right) Corners() =>
        (Children.Count > 0 ? Children[0] : null, Children.Count > 1 ? Children[1] : null);

    private static void OnGapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((BadgeEdge)d).InvalidateMeasure();
}
