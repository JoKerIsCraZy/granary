using System.Runtime.CompilerServices;
using Granary.App.Controls;
using Granary.App.Views;
using Granary.Core.Abstractions;
using Granary.Core.Navigation;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;

namespace Granary.App;

// A TV remote on a media PC, or the keyboard alone, as Silo's TV app: the arrows move focus, OK picks, and Back climbs out
// (BackLadder). Remotes send keys: the arrows, Enter, and Esc, Backspace or Browser Back for their Back button. Focus is
// remembered per page, so Back and Right out of the sidebar return to the card it was on.
public sealed partial class MainWindow
{
    // How long a page opened or returned to may take to show the element focus belongs on, and how often it is looked for.
    private static readonly TimeSpan ArrivalPatience = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ArrivalPoll = TimeSpan.FromMilliseconds(100);

    // Focus that stays this many looks is the page's own.
    private const int HeldPolls = 3;

    // Each page's last focused element.
    private readonly ConditionalWeakTable<Page, FocusMark> _pageFocus = [];

    // The open menus and dialogs whose keys the window follows, so the remote's Back closes them as Esc does.
    private readonly ConditionalWeakTable<UIElement, Popup> _followedPopups = [];

    // Whether the page the frame is changing to was asked for with a key: then focus goes to it.
    private bool _openedByKey;
    private bool _backByKey;

    // Every key the window sees: a page waiting to place focus stops once the viewer presses one.
    private int _keysPressed;

    private void InitializeRemote()
    {
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: false);
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, _) => _keysPressed++), handledEventsToo: true);
        FocusManager.GotFocus += OnFocusMoved;
        ContentFrame.Navigating += (_, _) => _openedByKey = FocusedElement() is UIElement { FocusState: FocusState.Keyboard };
        ContentFrame.Navigated += OnFrameNavigated;
    }

    // Keys no page or control took. Modifier combinations are the app's shortcuts (Alt+Left, Ctrl+K).
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HasModifier())
        {
            return;
        }
        bool repeat = e.KeyStatus.WasKeyDown;
        e.Handled = e.Key switch
        {
            VirtualKey.Escape when !repeat => Back(BackKey.Escape),
            VirtualKey.Back when !repeat => Back(BackKey.Backspace),
            VirtualKey.GoBack when !repeat => Back(BackKey.BrowserBack),
            VirtualKey.Left => EnterSidebar(repeat),
            VirtualKey.Right => LeaveSidebar(repeat),
            _ => false,
        };
    }

    private bool Back(BackKey key)
    {
        if (!BackLadder.IsBack(key, IsTyping()))
        {
            return false;
        }
        if (CloseTrailer())
        {
            return true;
        }
        var context = new BackContext(AreaOf(FocusedElement()), _navigation.IsAtSection, _navigation.Current == AppRoute.Home,
            _navigation.CanGoBack);
        switch (BackLadder.Decide(context))
        {
            case BackStep.GoBack:
                _backByKey = true;
                _navigation.GoBack();
                return true;
            case BackStep.FocusSidebar:
                return FocusSidebarEntry();
            case BackStep.GoHomeInSidebar:
                _navigation.GoBack();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusSidebarEntry());
                return true;
            default:
                return false;
        }
    }

    // Left at the page's left edge goes to the section's entry, not to whichever entry is nearest, as Up into the TV app's
    // menu bar does. A held key stops at the edge: crossing into the sidebar takes a new press.
    private bool EnterSidebar(bool repeat)
    {
        if (AreaOf(FocusedElement()) != FocusArea.Content || Root.XamlRoot is not { } root)
        {
            return false;
        }
        var next = FocusManager.FindNextElement(FocusNavigationDirection.Left, new FindNextElementOptions { SearchRoot = root.Content });
        if (next is not null && AreaOf(next) == FocusArea.Content)
        {
            return false;
        }
        return repeat || FocusSidebarEntry();
    }

    // Right out of the sidebar goes back to where focus was on the page, else to its first element.
    private bool LeaveSidebar(bool repeat)
    {
        if (repeat || AreaOf(FocusedElement()) != FocusArea.Sidebar || ContentFrame.Content is not Page page)
        {
            return false;
        }
        _pageFocus.TryGetValue(page, out var mark);
        return (RestoreFocus(page, mark) ?? FocusFirst(page)) is not null;
    }

    // A card the keyboard moves to comes to the middle of the page, up and down, as the TV app keeps its focused row in one
    // place: scrolled only into view, a card in the lowest row lay against the window's edge with its caption cut off.
    // Asked after the scroll XAML starts for the focus itself, which would otherwise win.
    private void CenterRow(UIElement element)
    {
        if (element is Button { Content: MediaCardView, FocusState: FocusState.Keyboard } card)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (card.FocusState == FocusState.Keyboard)
                {
                    card.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5, AnimationDesired = true });
                }
            });
        }
    }

    private bool FocusSidebarEntry() =>
        Navigation.IsPaneVisible && Navigation.SelectedItem is NavigationViewItem entry && entry.Focus(FocusState.Keyboard);

    private void OnFocusMoved(object? sender, FocusManagerGotFocusEventArgs args)
    {
        if (args.NewFocusedElement is not UIElement element)
        {
            return;
        }
        if (ContentFrame.Content is Page page && IsWithin(element, page))
        {
            _pageFocus.AddOrUpdate(page, FocusMark.Of(element));
            CenterRow(element);
            return;
        }
        FollowPopupOf(element);
    }

    // A page opened with a key starts on its arrival element (a title's Play), and a page Back returns to on the card it
    // left from. Both wait for the page to show its parts (a title loads, a grid makes its cards), and hold the place
    // against the focus XAML gives a page that comes in, until the viewer presses a key.
    private async void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        bool back = e.NavigationMode == NavigationMode.Back && _backByKey;
        bool opened = e.NavigationMode == NavigationMode.New && _openedByKey;
        _backByKey = _openedByKey = false;
        if (e.Content is not Page page || !(back || opened))
        {
            return;
        }
        // Taken now: the focus XAML gives the page as it comes in would replace it.
        _pageFocus.TryGetValue(page, out var mark);
        int keys = _keysPressed;
        try
        {
            UIElement? placed = null;
            int held = 0;
            for (var waited = TimeSpan.Zero; waited < ArrivalPatience && held < HeldPolls; waited += ArrivalPoll)
            {
                await Task.Delay(ArrivalPoll);
                // Gone, or the viewer moved on (a section's own page keeps focus in the sidebar, as the TV app's tabs do).
                if (!ReferenceEquals(ContentFrame.Content, page) || (opened && _navigation.IsAtSection) || _keysPressed != keys)
                {
                    return;
                }
                if (placed is not null && ReferenceEquals(FocusedElement(), placed))
                {
                    held++;
                    continue;
                }
                placed = back ? RestoreFocus(page, mark) : FocusArrival(page);
                held = 0;
            }
            if (placed is null && ReferenceEquals(ContentFrame.Content, page) && _keysPressed == keys)
            {
                FocusFirst(page);
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            _logger.LogError(ex, "Focus could not be placed on the page");
        }
    }

    // The element focused, or null while the page has none to take it yet.
    private static UIElement? FocusArrival(Page page)
    {
        if (page is not IArrivalFocus arrival)
        {
            return FocusFirst(page);
        }
        return arrival.ArrivalFocus is { IsLoaded: true, IsEnabled: true, Visibility: Visibility.Visible } target
            && target.Focus(FocusState.Keyboard) ? target : null;
    }

    // The page's first card, as the TV app starts on the first row's first card; a page without cards on its first
    // element that takes focus.
    private static UIElement? FocusFirst(Page page) =>
        (FirstCard(page) ?? FocusManager.FindFirstFocusableElement(page)) is UIElement first && first.Focus(FocusState.Keyboard)
            ? first : null;

    // In the order the lists have their titles: a repeater's children are in the order it made them.
    private static Control? FirstCard(DependencyObject element)
    {
        if (element is UIElement { Visibility: Visibility.Collapsed })
        {
            return null;
        }
        if (element is Button { Content: MediaCardView, IsEnabled: true } card)
        {
            return card;
        }
        var children = Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(element)).Select(i => VisualTreeHelper.GetChild(element, i));
        if (element is ItemsRepeater repeater)
        {
            // Parked (recycled) elements have no index.
            children = children.OfType<UIElement>()
                .Select(child => (Child: child, Index: repeater.GetElementIndex(child)))
                .Where(entry => entry.Index >= 0)
                .OrderBy(entry => entry.Index)
                .Select(entry => (DependencyObject)entry.Child);
        }
        return children.Select(FirstCard).FirstOrDefault(found => found is not null);
    }

    // The element that last had focus on the page; a card that now shows another title, or is gone, is found again by its
    // title in its list, else by its place (ReturnTarget).
    private static UIElement? RestoreFocus(Page page, FocusMark? mark)
    {
        if (mark is null)
        {
            return null;
        }
        if (mark.Element.TryGetTarget(out var element) && element.XamlRoot is not null && IsWithin(element, page)
            && (mark.Card is null || element is Button { Content: MediaCardView { Card: { } shown } } && shown.Id == mark.Card.Id))
        {
            return element.Focus(FocusState.Keyboard) ? element : null;
        }
        if (mark.Card is not { } card || mark.Repeater?.TryGetTarget(out var repeater) != true
            || repeater!.ItemsSourceView is not { Count: > 0 } items)
        {
            return null;
        }
        var cards = Enumerable.Range(0, items.Count).Select(i => items.GetAt(i) as MediaCard).ToList();
        int index = ReturnTarget.IndexFor(cards, card, mark.Index, each => each?.Id);
        if (index < 0 || repeater.GetOrCreateElement(index) is not Control found)
        {
            return null;
        }
        found.StartBringIntoView();
        return found.Focus(FocusState.Keyboard) ? found : null;
    }

    // Menus, flyouts and dialogs close on Esc themselves, not on a remote's Back: their keys do not reach the window.
    private void FollowPopupOf(UIElement element)
    {
        if (Root.XamlRoot is not { } root)
        {
            return;
        }
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.Child is { } child && IsWithin(element, child) && !_followedPopups.TryGetValue(child, out _))
            {
                _followedPopups.Add(child, popup);
                child.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnPopupKeyDown), handledEventsToo: false);
            }
        }
    }

    private void OnPopupKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = e.Key switch
        {
            VirtualKey.GoBack => BackKey.BrowserBack,
            VirtualKey.Back => BackKey.Backspace,
            _ => (BackKey?)null,
        };
        if (key is not { } back || e.KeyStatus.WasKeyDown || !BackLadder.IsBack(back, IsTyping())
            || sender is not UIElement child || !_followedPopups.TryGetValue(child, out var popup))
        {
            return;
        }
        e.Handled = true;
        if (FindAncestorOrSelf<ContentDialog>(FocusedElement()) is { } dialog)
        {
            dialog.Hide();
        }
        else
        {
            popup.IsOpen = false;
        }
    }

    private FocusArea AreaOf(DependencyObject? element) =>
        element is null ? FocusArea.None
        : IsWithin(element, ContentFrame) ? FocusArea.Content
        : IsWithin(element, Navigation) ? FocusArea.Sidebar
        : FocusArea.None;

    private DependencyObject? FocusedElement() =>
        Root.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;

    private bool IsTyping() => FocusedElement() is TextBox or PasswordBox or RichEditBox;

    private static bool HasModifier() =>
        IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.Shift)
        || IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows);

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor) =>
        FindAncestorOrSelf<DependencyObject>(element, candidate => ReferenceEquals(candidate, ancestor)) is not null;

    private static T? FindAncestorOrSelf<T>(DependencyObject? element, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T found && (match is null || match(found)))
            {
                return found;
            }
        }
        return null;
    }

    // Where focus was on a page; for a card, also the title it showed and its list and place, to find the title again.
    private sealed record FocusMark(WeakReference<UIElement> Element, MediaCard? Card, int Index, WeakReference<ItemsRepeater>? Repeater)
    {
        public static FocusMark Of(UIElement element)
        {
            if (element is Button { Content: MediaCardView { Card: { IsPlaceholder: false } card } }
                && FindAncestorOrSelf<ItemsRepeater>(VisualTreeHelper.GetParent(element)) is { } repeater)
            {
                return new FocusMark(new(element), card, repeater.GetElementIndex(element), new(repeater));
            }
            return new FocusMark(new(element), null, -1, null);
        }
    }
}
