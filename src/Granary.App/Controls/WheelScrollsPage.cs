using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Granary.App.Controls;

/// <summary>
/// For a sideways row inside a page: the mouse wheel scrolls the page, as anywhere else on it, and the row only with
/// Shift held or a tilting wheel. The row ignores the wheel (<see cref="ScrollView.IgnoredInputKinds"/>) instead of
/// catching it on its way to it: WinUI treats the wheel as an inertial manipulation, so turns that come quickly reach
/// the scroller under the pointer past the routed events, and a row that only listened took them sideways.
/// </summary>
public static class WheelScrollsPage
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(WheelScrollsPage), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(ScrollView row) => (bool)row.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollView row, bool value) => row.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ScrollView row)
        {
            return;
        }
        if (e.NewValue is true)
        {
            row.IgnoredInputKinds |= ScrollingInputKinds.MouseWheel;
            row.PointerWheelChanged += OnWheel;
        }
        else
        {
            row.IgnoredInputKinds &= ~ScrollingInputKinds.MouseWheel;
            row.PointerWheelChanged -= OnWheel;
        }
    }

    // An upright turn passes on to the page; a tilt, or a turn with Shift, scrolls the row by the wheel's own step.
    private static void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var row = (ScrollView)sender;
        var wheel = e.GetCurrentPoint(row).Properties;
        bool tilted = wheel.IsHorizontalMouseWheel;
        if (!tilted && !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            return;
        }
        e.Handled = true;
        // A tilt to the right is positive; a turn away from the user is too, and goes back along the row.
        double step = tilted ? wheel.MouseWheelDelta : -wheel.MouseWheelDelta;
        row.ScrollBy(step, 0);
    }
}
