using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Granary.App.Controls;

/// <summary>
/// For a sideways row inside a page: the mouse wheel scrolls the page, as anywhere else on it, and the row only with
/// Shift held or a tilting wheel. On its own, a ScrollViewer that scrolls only sideways takes every turn of the wheel,
/// which holds the page still while the pointer is over a row.
/// </summary>
public static class WheelScrollsPage
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(WheelScrollsPage), new PropertyMetadata(false, OnIsEnabledChanged));

    // Where a page is headed while a wheel scroll animates, so quick turns add up instead of starting over from where
    // the animation is; forgotten once the page comes to rest.
    private static readonly ConditionalWeakTable<ScrollViewer, Destination> Destinations = new();

    public static bool GetIsEnabled(ScrollViewer scroller) => (bool)scroller.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer scroller, bool value) => scroller.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is ScrollViewer row)
        {
            // The content sees the wheel before the row's ScrollViewer does; it is there once the row is loaded, and
            // a bound one may come later.
            row.Loaded += (_, _) => Attach(row);
            row.RegisterPropertyChangedCallback(ContentControl.ContentProperty, (_, _) => Attach(row));
        }
    }

    private static void Attach(ScrollViewer row)
    {
        if (row.Content is UIElement content && !GetAttached(content))
        {
            content.SetValue(AttachedProperty, true);
            content.PointerWheelChanged += (_, e) => OnWheel(row, e);
        }
    }

    private static void OnWheel(ScrollViewer row, PointerRoutedEventArgs e)
    {
        var wheel = e.GetCurrentPoint(row).Properties;
        if (wheel.IsHorizontalMouseWheel || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) || PageOf(row) is not { } page)
        {
            return;
        }
        e.Handled = true;
        var destination = Destinations.GetValue(page, Follow);
        double from = destination.Offset ?? page.VerticalOffset;
        destination.Offset = Math.Clamp(from - wheel.MouseWheelDelta, 0, page.ScrollableHeight);
        page.ChangeView(null, destination.Offset, null);
    }

    private static ScrollViewer? PageOf(ScrollViewer row)
    {
        for (var parent = VisualTreeHelper.GetParent(row); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer { VerticalScrollMode: not ScrollMode.Disabled } page)
            {
                return page;
            }
        }
        return null;
    }

    private static Destination Follow(ScrollViewer page)
    {
        var destination = new Destination();
        page.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate)
            {
                destination.Offset = null;
            }
        };
        return destination;
    }

    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(WheelScrollsPage), new PropertyMetadata(false));

    private static bool GetAttached(UIElement element) => (bool)element.GetValue(AttachedProperty);

    private sealed class Destination
    {
        public double? Offset { get; set; }
    }
}
