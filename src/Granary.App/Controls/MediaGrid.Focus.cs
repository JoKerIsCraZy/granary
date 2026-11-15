using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

// A card that had the focus and goes from the list (a download deleted from its ⋮ menu) hands the focus on to the card now
// in its place, or to the last one, so a keyboard, a controller or a remote carries on through the grid rather than losing
// its place, as the Downloads page's rows did; when it was the grid's last card, the page above it says where the focus
// goes (ICardFocusHost). Only when the focus went nowhere: the repeater may have moved it to a card beside it already, and
// focus the viewer moved elsewhere stays there.
public sealed partial class MediaGrid
{
    // The place of the card that last took the focus.
    private int _focusedIndex = -1;

    /// <summary>Focuses the card at <paramref name="index"/> (the last one past the end); false when the grid has none.</summary>
    public bool FocusCardAt(int index, FocusState state)
    {
        int count = Items.ItemsSourceView?.Count ?? 0;
        if (count == 0 || !IsLoaded)
        {
            return false;
        }
        return Items.GetOrCreateElement(Math.Clamp(index, 0, count - 1)) is Control card && card.Focus(state);
    }

    private void NoteFocusedCard(object sender)
    {
        if (sender is UIElement element)
        {
            _focusedIndex = Items.GetElementIndex(element);
        }
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (_focusedIndex < 0 || XamlRoot is not { } root || FocusManager.GetFocusedElement(root) is not DependencyObject focused
            || !IsWithin(focused, args.Element))
        {
            return;
        }
        var state = focused is Control { FocusState: FocusState.Keyboard } ? FocusState.Keyboard : FocusState.Programmatic;
        int place = _focusedIndex;
        // Found now, while the grid is still in its page.
        var host = FocusHostOf(this);
        // After the list's change is laid out.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusCardAfterRemoval(root, place, state, host));
    }

    private void FocusCardAfterRemoval(XamlRoot root, int place, FocusState state, ICardFocusHost? host)
    {
        var focused = FocusManager.GetFocusedElement(root) as DependencyObject;
        bool lost = focused is null || focused is FrameworkElement { IsLoaded: false } || ReferenceEquals(focused, _scroller);
        if (lost && !FocusCardAt(place, state))
        {
            host?.FocusAfterLastCard(this, state);
        }
    }

    private static ICardFocusHost? FocusHostOf(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ICardFocusHost host)
            {
                return host;
            }
        }
        return null;
    }

    /// <summary>Whether <paramref name="element"/> is <paramref name="container"/> or inside it.</summary>
    internal static bool IsWithin(DependencyObject element, UIElement container)
    {
        for (var parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (ReferenceEquals(parent, container))
            {
                return true;
            }
        }
        return false;
    }
}
