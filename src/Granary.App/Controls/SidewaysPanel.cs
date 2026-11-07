using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// Lays its <see cref="Child"/> on its side, turned a quarter to the left so it reads from bottom to top, as the web's
/// vertical labels (vertical-rl text turned 180°), centred in the room it is given. It takes the room the turned child
/// needs, which a RenderTransform alone does not: layout would still give the child its unturned width.
/// </summary>
internal sealed partial class SidewaysPanel : Panel
{
    public static readonly DependencyProperty ChildProperty = DependencyProperty.Register(
        nameof(Child), typeof(UIElement), typeof(SidewaysPanel), new PropertyMetadata(null, OnChildChanged));

    private const double QuarterLeft = -90;

    /// <summary>The element laid on its side; the panel's only child.</summary>
    public UIElement? Child
    {
        get => (UIElement?)GetValue(ChildProperty);
        set => SetValue(ChildProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }
        // The child's width runs along the panel's height, and its height across the panel's width.
        child.Measure(new Size(availableSize.Height, availableSize.Width));
        return new Size(child.DesiredSize.Height, child.DesiredSize.Width);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { DesiredSize: var size } child)
        {
            double left = Math.Max(0, (finalSize.Width - size.Height) / 2);
            double top = Math.Max(0, (finalSize.Height - size.Width) / 2);
            // Turned about its top left corner, the child would stand above where it is placed: it starts at its bottom.
            child.Arrange(new Rect(left, top + size.Width, size.Width, size.Height));
        }
        return finalSize;
    }

    // The turn is set once, as the child comes in: a recycled separator keeps its child and the turn with it.
    private static void OnChildChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var panel = (SidewaysPanel)sender;
        panel.Children.Clear();
        if (e.NewValue is UIElement child)
        {
            child.RenderTransform = new RotateTransform { Angle = QuarterLeft };
            panel.Children.Add(child);
        }
    }
}
