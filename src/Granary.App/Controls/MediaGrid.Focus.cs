using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

// A card that had the focus and goes from the list (a download deleted from its ⋮ menu) hands the focus on to the card now
// in its place, or to the last one, so a keyboard, a controller or a remote carries on through the grid rather than losing
// its place, as the Downloads page's rows did. Only when the focus went nowhere: the repeater may have moved it to a card
// beside it already, and focus the viewer moved elsewhere stays there.
public sealed partial class MediaGrid
{
    // The place of the card that last took the focus.
    private int _focusedIndex = -1;

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
        // After the list's change is laid out.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusCardAfterRemoval(place, state));
    }

    private void FocusCardAfterRemoval(int place, FocusState state)
    {
        int count = Items.ItemsSourceView?.Count ?? 0;
        if (!IsLoaded || count == 0 || XamlRoot is not { } root)
        {
            return;
        }
        var focused = FocusManager.GetFocusedElement(root) as DependencyObject;
        bool lost = focused is null || focused is FrameworkElement { IsLoaded: false } || ReferenceEquals(focused, _scroller);
        if (!lost)
        {
            return;
        }
        if (Items.GetOrCreateElement(Math.Clamp(place, 0, count - 1)) is Control card)
        {
            card.Focus(state);
        }
    }

    private static bool IsWithin(DependencyObject element, UIElement container)
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
