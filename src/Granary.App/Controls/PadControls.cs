using Granary.Core.Controller;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Granary.App.Controls;

/// <summary>
/// What a game controller does to the control that has focus. A pad sends no keys in a desktop app, so the window carries
/// its buttons out itself (MainWindow.Controller.cs): focus moves through the same search the arrow keys use and shows as
/// the keyboard's, and A presses a control the way a screen reader does, through its automation peer.
/// </summary>
internal static class PadControls
{
    public static FocusNavigationDirection WayOf(PadDirection direction) => direction switch
    {
        PadDirection.Up => FocusNavigationDirection.Up,
        PadDirection.Down => FocusNavigationDirection.Down,
        PadDirection.Left => FocusNavigationDirection.Left,
        PadDirection.Right => FocusNavigationDirection.Right,
        _ => FocusNavigationDirection.None,
    };

    /// <summary>Moves focus one step within <paramref name="scope"/>; false when nothing lies that way.</summary>
    public static bool Move(FocusNavigationDirection way, DependencyObject scope) =>
        FocusManager.FindNextElement(way, new FindNextElementOptions { SearchRoot = scope }) is { } next && Focus(next);

    /// <summary>Focus as the keyboard's, so its ring shows: a pad moves it as the arrow keys do.</summary>
    public static bool Focus(DependencyObject element)
    {
        if (element is UIElement target)
        {
            return target.Focus(FocusState.Keyboard);
        }
        // A link in a text.
        _ = FocusManager.TryFocusAsync(element, FocusState.Keyboard);
        return true;
    }

    /// <summary>
    /// Left and Right on a slider, the seek bar or the speed track change its value, as the arrow keys do there; Up and
    /// Down on an upright slider. False for anything else, and for the other two directions, which move focus on.
    /// </summary>
    public static bool Nudge(DependencyObject focused, FocusNavigationDirection way)
    {
        bool isUpright = focused is Slider { Orientation: Orientation.Vertical };
        int sign = (isUpright, way) switch
        {
            (false, FocusNavigationDirection.Right) or (true, FocusNavigationDirection.Up) => 1,
            (false, FocusNavigationDirection.Left) or (true, FocusNavigationDirection.Down) => -1,
            _ => 0,
        };
        if (sign == 0 || focused is not UIElement { } element || (focused is Control { IsEnabled: false }))
        {
            return false;
        }
        if (element is SeekBar bar)
        {
            bar.RequestSeek(bar.DisplayedSeconds + (sign > 0 ? bar.ForwardStepSeconds : -bar.BackStepSeconds));
            return true;
        }
        if (FrameworkElementAutomationPeer.CreatePeerForElement(element)?.GetPattern(PatternInterface.RangeValue)
            is not IRangeValueProvider { IsReadOnly: false } range)
        {
            return false;
        }
        range.SetValue(Math.Clamp(range.Value + (sign * range.SmallChange), range.Minimum, range.Maximum));
        return true;
    }

    /// <summary>
    /// A on the focused control: a button is clicked, a switch or check box flipped, a tab or pill chosen, a drop-down
    /// opened and its row picked. A text field does nothing yet: typing with a pad needs a keyboard on screen.
    /// </summary>
    public static bool Press(DependencyObject focused)
    {
        // A clicks a card's button with no key on the way: a pointer's press on its title line that is still noted must
        // not turn this click into the series' (MediaCardView.TryOpenHeading), as no key's click does.
        if (focused is ButtonBase { Content: MediaCardView card })
        {
            card.ForgetPress();
        }
        switch (focused)
        {
            case Control { IsEnabled: false }:
            case TextBox or PasswordBox or RichEditBox or AutoSuggestBox:
                return false;
            case RadioButton radio:
                radio.IsChecked = true;
                return true;
            case ToggleSwitch toggle:
                toggle.IsOn = !toggle.IsOn;
                return true;
            case ComboBox combo:
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
                return true;
            case ComboBoxItem item when ItemsControl.ItemsControlFromItemContainer(item) is ComboBox owner:
                owner.SelectedIndex = owner.IndexFromContainer(item);
                owner.IsDropDownOpen = false;
                return true;
        }
        if (focused is not UIElement element || PeerOf(element) is not { } peer || !peer.IsEnabled())
        {
            return false;
        }
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
        {
            invoke.Invoke();
        }
        else if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
        {
            toggle.Toggle();
        }
        else if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selection)
        {
            selection.Select();
        }
        else if (peer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expander)
        {
            if (expander.ExpandCollapseState == Microsoft.UI.Xaml.Automation.ExpandCollapseState.Collapsed)
            {
                expander.Expand();
            }
            else
            {
                expander.Collapse();
            }
        }
        else
        {
            return false;
        }
        return true;
    }

    // A list's row is pressed and chosen through its list: the row's own peer offers neither.
    private static AutomationPeer? PeerOf(UIElement element) =>
        element is SelectorItem row && ItemsControl.ItemsControlFromItemContainer(row) is { } list
            && FrameworkElementAutomationPeer.CreatePeerForElement(list) is ItemsControlAutomationPeer owner
            && owner.CreateItemAutomationPeer(list.ItemFromContainer(row)) is { } item
                ? item
                : FrameworkElementAutomationPeer.CreatePeerForElement(element);
}
