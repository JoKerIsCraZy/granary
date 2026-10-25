using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// Rounds an element's ends into half circles at any size, as CSS's <c>rounded-full</c> does on Silo's web. WinUI
/// does not clamp a corner radius larger than half the element: it scales the corners into an ellipse instead, so a
/// 999 px radius turns a 44 px high button into an oval. This keeps the radius at half the shorter side.
/// </summary>
public static class Pill
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(Pill), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }
        element.SizeChanged -= OnSizeChanged;
        if ((bool)e.NewValue)
        {
            element.SizeChanged += OnSizeChanged;
            Round(element, element.ActualWidth, element.ActualHeight);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        Round((FrameworkElement)sender, e.NewSize.Width, e.NewSize.Height);

    private static void Round(FrameworkElement element, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }
        var radius = new CornerRadius(Math.Min(width, height) / 2);
        switch (element)
        {
            case Control control:
                control.CornerRadius = radius;
                break;
            case Border border:
                border.CornerRadius = radius;
                break;
            case Grid grid:
                grid.CornerRadius = radius;
                break;
            case StackPanel panel:
                panel.CornerRadius = radius;
                break;
        }
    }
}
