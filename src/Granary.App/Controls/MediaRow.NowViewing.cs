using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

// A row holding the page's own title among its siblings (MediaCard.IsCurrent: an episode's More Episodes) opens with that
// card in its middle, as the web's EpisodeCarousel centres it, and Up or Down into the row lands on it, as the TV app's
// episode rail starts on the episode its page is about. Rows without a marked card do neither.
public sealed partial class MediaRow
{
    // Layout passes the row waits for its list to be laid out in full before it centres as well as it can and stops.
    private const int CentringPasses = 20;

    // The marked card's place in the row, or -1.
    private int _currentIndex = -1;
    // The list still to be scrolled to its marked card, the passes waited for it, and whether layout is followed for it.
    private object? _listToCentre;
    private int _centringPasses;
    private bool _followsLayout;

    private void FindCurrent()
    {
        _currentIndex = -1;
        if (ItemsSource is IReadOnlyList<MediaCard> cards)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].IsCurrent)
                {
                    _currentIndex = i;
                    break;
                }
            }
        }
        _listToCentre = _currentIndex >= 0 ? ItemsSource : null;
        _centringPasses = 0;
        // Another season's list starts from its beginning, not where the last one was scrolled to.
        if (_listToCentre is not null && Scroller.HorizontalOffset > 0)
        {
            Scroller.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
        }
        FollowLayout(IsLoaded);
    }

    // Layout is followed only while the row is in the tree and still to be scrolled.
    private void FollowLayout(bool loaded)
    {
        bool follows = loaded && _listToCentre is not null;
        if (follows == _followsLayout)
        {
            return;
        }
        _followsLayout = follows;
        if (follows)
        {
            LayoutUpdated += OnLayoutUpdated;
        }
        else
        {
            LayoutUpdated -= OnLayoutUpdated;
        }
    }

    // Once the repeater shows the new list laid out and the scroller has taken in enough of it (RowCentring), and at most
    // CentringPasses passes, so a row that stays hidden or never lays out a card stops listening. A card that already
    // has focus (Back to the page puts it on the card it left from) keeps the row where that card shows.
    private void OnLayoutUpdated(object? sender, object e)
    {
        bool lastTry = ++_centringPasses >= CentringPasses;
        bool shown = Visibility == Visibility.Visible && ReferenceEquals(Items.ItemsSource, _listToCentre);
        if (HasFocusWithin() || (shown && RowBleed.Centre(Scroller, Items, _currentIndex, lastTry)) || lastTry)
        {
            _listToCentre = null;
            FollowLayout(loaded: false);
        }
    }

    // Only an arrow that comes from outside the row: within it, Left and Right go card by card. A marked card scrolled
    // out of the cards the repeater keeps is made again and brought to the middle, as MainWindow's CardInList finds a card.
    private void OnItemsGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (args.Direction is not (FocusNavigationDirection.Up or FocusNavigationDirection.Down)
            || (args.OldFocusedElement is { } old && IsWithinItems(old)) || ShownCurrentIndex() is not { } index)
        {
            return;
        }
        var current = Items.TryGetElement(index);
        if (current is null)
        {
            current = Items.GetOrCreateElement(index);
            current.StartBringIntoView(new BringIntoViewOptions { HorizontalAlignmentRatio = 0.5, AnimationDesired = false });
        }
        if (!ReferenceEquals(args.NewFocusedElement, current))
        {
            args.TrySetNewFocusedElement(current);
        }
    }

    // The marked card's place while the repeater shows the list it was found in.
    private int? ShownCurrentIndex() =>
        _currentIndex >= 0 && Items.ItemsSourceView is { } shown && _currentIndex < shown.Count
            && shown.GetAt(_currentIndex) is MediaCard { IsCurrent: true }
            ? _currentIndex
            : null;

    private bool HasFocusWithin() =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && IsWithinItems(focused);

    private bool IsWithinItems(DependencyObject element)
    {
        for (var parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (ReferenceEquals(parent, Items))
            {
                return true;
            }
        }
        return false;
    }
}
