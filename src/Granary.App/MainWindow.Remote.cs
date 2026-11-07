using System.Runtime.CompilerServices;
using Granary.App.Controls;
using Granary.App.Interop;
using Granary.App.Views;
using Granary.Core.Abstractions;
using Granary.Core.Navigation;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.DependencyInjection;
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

    // Each page's last focused element, and the same for the pages in the history: a title's page is made again when Back
    // returns to it, and its mark goes with its history entry.
    private readonly ConditionalWeakTable<Page, FocusMark> _pageFocus = [];
    private readonly ConditionalWeakTable<PageStackEntry, FocusMark> _entryFocus = [];
    private Page? _leaving;
    private PageStackEntry? _returning;

    // The open menus and dialogs whose keys the window follows, so the remote's Back closes them as Esc does.
    private readonly ConditionalWeakTable<UIElement, Popup> _followedPopups = [];

    // Whether the page the frame is changing to was asked for with a key: then focus goes to it.
    private bool _openedByKey;
    private bool _openedFromSidebar;
    private bool _backByKey;

    // A remote's Play/Pause (and the Play some remotes have): media keys, which Windows hands the focused window first.
    private const VirtualKey MediaPlayPause = (VirtualKey)0xB3;
    private const VirtualKey MediaPlay = (VirtualKey)0xFA;

    // Every key and mouse button the window sees: a page waiting to place focus stops once the viewer presses one.
    private int _keysPressed;
    private int _pointersPressed;

    // Whether the viewer last pressed a key rather than a mouse button: focus given back after the player shows then.
    private bool _lastInputWasKey;

    // The focus the start placed on its first page, not shown until the remote's first key.
    private readonly QuietStartFocus _startFocus = new();

    private void InitializeRemote()
    {
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: false);
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnRemotePreviewKeyDown), handledEventsToo: true);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _lastInputWasKey = false;
            _pointersPressed++;
            _startFocus.End();
        }), handledEventsToo: true);
        FocusManager.GotFocus += OnFocusMoved;
        ContentFrame.Navigating += (_, e) =>
        {
            _openedByKey = FocusedElement() is UIElement { FocusState: FocusState.Keyboard };
            _openedFromSidebar = _openedByKey && AreaOf(FocusedElement()) == FocusArea.Sidebar;
            _leaving = ContentFrame.Content as Page;
            _returning = e.NavigationMode == NavigationMode.Back ? ContentFrame.BackStack.LastOrDefault() : null;
        };
        ContentFrame.Navigated += OnFrameNavigated;
        WindowMessageHook.Install(WinRT.Interop.WindowNative.GetWindowHandle(this), TakeAppCommand);
    }

    // A remote's Play sent as a command, not as a key: on the focused card or title, as the Play key, while nothing plays;
    // while something plays Windows hands it to the media session.
    private bool TakeAppCommand(int command)
    {
        try
        {
            return command == WindowMessageHook.MediaPlay && PlayFocused();
        }
        catch (Exception ex)
        {
            // Called from the window's message loop: nothing may escape.
            _logger.LogError(ex, "A remote's Play command could not be handled");
            return false;
        }
    }

    // On the way down, before XAML moves focus with an arrow: once a key has reached the window, focus has moved.
    private void OnRemotePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _keysPressed++;
        _lastInputWasKey = true;
        if (e.Handled || HasModifier())
        {
            return;
        }
        if (ShowsStartFocus(e.Key))
        {
            e.Handled = true;
            return;
        }
        bool repeat = e.KeyStatus.WasKeyDown;
        e.Handled = e.Key switch
        {
            VirtualKey.Right => MovesAlongDock(FocusNavigationDirection.Right) || StopsAtRowEnd() || LeavesRadioGroup(FocusNavigationDirection.Right, repeat),
            VirtualKey.Left => MovesAlongDock(FocusNavigationDirection.Left) || LeavesRadioGroup(FocusNavigationDirection.Left, repeat),
            VirtualKey.Down => MovesAlongDock(FocusNavigationDirection.Down) || EntersDock(repeat) || LeavesRadioGroup(FocusNavigationDirection.Down, repeat),
            VirtualKey.Up => MovesAlongDock(FocusNavigationDirection.Up) || LeavesRadioGroup(FocusNavigationDirection.Up, repeat),
            _ => false,
        };
    }

    // The first arrow or OK on the start's quiet focus shows it where it is, and moves or opens nothing (QuietStartFocus).
    // A keyboard's OK is Space as often as Enter.
    private bool ShowsStartFocus(VirtualKey key)
    {
        var focused = FocusedElement();
        bool isArrowOrOk = key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Enter
            or VirtualKey.Space;
        return _startFocus.ShowsInPlace(isArrowOrOk, focused, focused is UIElement { FocusState: FocusState.Keyboard },
            () => focused is UIElement element && element.Focus(FocusState.Keyboard));
    }

    // The minimized player's bar lies below the page, outside it: Down from the page's bottom goes to its picture, which
    // brings the player back, so a remote that minimized the player can return to it.
    private bool EntersDock(bool repeat)
    {
        var focused = FocusedElement();
        if (repeat || _player is not { IsDocked: true } player || AreaOf(focused) != FocusArea.Content || KeepsArrows(focused)
            || Root.XamlRoot is not { } root)
        {
            return false;
        }
        var next = FocusManager.FindNextElement(FocusNavigationDirection.Down, new FindNextElementOptions { SearchRoot = root.Content });
        return (next is null || AreaOf(next) != FocusArea.Content) && player.FocusDock();
    }

    // In the bar Left and Right move along it, except on its seek bar, which seeks with them; Up goes back to the page.
    private bool MovesAlongDock(FocusNavigationDirection direction)
    {
        if (_player is not { IsDocked: true } || FocusedElement() is not DependencyObject focused || !IsWithin(focused, PlayerHost))
        {
            return false;
        }
        if (direction == FocusNavigationDirection.Up)
        {
            ReturnFocusToPage();
            return true;
        }
        // Nothing lies below the bar.
        if (direction == FocusNavigationDirection.Down)
        {
            return true;
        }
        if (KeepsArrows(focused))
        {
            return false;
        }
        FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = PlayerHost });
        return true;
    }

    // Controls that use the arrows themselves: a text field's caret, a list box's choice, a slider's or seek bar's value.
    private static bool KeepsArrows(DependencyObject? element) =>
        element is TextBox or PasswordBox or RichEditBox or ComboBox or RangeBase or SeekBar;

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
            MediaPlayPause or MediaPlay when !repeat => PlayFocused(),
            VirtualKey.Application when !repeat => FocusedElement() is Button { Content: MediaCardView card } && card.ShowMenuFromKey(),
            _ => false,
        };
    }

    private bool Back(BackKey key)
    {
        // The full player's Back keys are its own: one it lets pass (a panel or menu of its own is open) closes nothing more.
        if (!BackLadder.IsBack(key, IsTyping()) || IsPlayerFull)
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
        // A title's page has no entry selected: the nearest one then, as the arrow alone would.
        return repeat || FocusSidebarEntry() || (next is NavigationViewItem nearest && nearest.Focus(FocusState.Keyboard));
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
        if (element is Button { FocusState: FocusState.Keyboard } card && IsCard(card))
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

    // A row's last card keeps focus on Right, as the TV app's rows stop at their end: the arrows went on to whatever lay
    // nearest, the "Explore all" link high up at the page's right edge.
    private bool StopsAtRowEnd()
    {
        if (FocusedElement() is not Button card || !IsCard(card) || Root.XamlRoot is not { } root
            || FindAncestorOrSelf<ItemsRepeater>(VisualTreeHelper.GetParent(card)) is not { Layout: StackLayout { Orientation: Orientation.Horizontal } } row)
        {
            return false;
        }
        var next = FocusManager.FindNextElement(FocusNavigationDirection.Right, new FindNextElementOptions { SearchRoot = root.Content });
        return next is null || !IsWithin(next, row);
    }

    // Play on a card plays it, as its play disc does, an episode of a title's page too; anywhere else on a title's page, its
    // Play. While something plays (the player full or as the bar), the key stays the media session's and pauses that.
    private bool PlayFocused()
    {
        if (IsPlayerOpen)
        {
            return false;
        }
        var focused = FocusedElement();
        if (IsCard(focused))
        {
            if (CardOf(focused) is not { } card || !MediaCardActions.CanPlay(card))
            {
                return false;
            }
            App.Services.GetRequiredService<MediaCardActions>().Play(card);
            return true;
        }
        return ContentFrame.Content is ItemDetailPage page && focused is not null && IsWithin(focused, page) && page.PlayFromKey();
    }

    // A card button: a poster or episode card of a list, or an episode on a title's page.
    private static bool IsCard(DependencyObject? element) => element is Button { Content: MediaCardView or DetailEpisodeCard };

    // The title a card button shows; a placeholder still loading shows none.
    private static MediaCard? CardOf(DependencyObject? element) => element switch
    {
        Button { Content: MediaCardView { Card: { IsPlaceholder: false } card } } => card,
        Button { Content: DetailEpisodeCard { Episode: { } episode } } => episode.Card,
        _ => null,
    };

    // A list's item as its title: the lists hold cards, a title's page its episodes.
    private static MediaCard? CardOfItem(object? item) => item switch
    {
        MediaCard card => card,
        DetailEpisode episode => episode.Card,
        _ => null,
    };

    // A radio button keeps every arrow to move along its group, and checks each one it comes to, so focus never left a row
    // of tabs (the library's Recommended / Library / Collections) or the Watch Party's Host picks / Everyone votes, and
    // Down switched tabs. An arrow towards another radio of the group still moves along it; any other way focus goes where
    // the arrow points (Left at the page's edge into the sidebar), and with nothing that way it stays.
    private bool LeavesRadioGroup(FocusNavigationDirection direction, bool repeat)
    {
        if (FocusedElement() is not RadioButton radio || AreaOf(radio) != FocusArea.Content || Root.XamlRoot is not { } root)
        {
            return false;
        }
        var next = FocusManager.FindNextElement(direction, new FindNextElementOptions { SearchRoot = root.Content });
        if (next is RadioButton other && IsSameGroup(radio, other))
        {
            return false;
        }
        if (next is not null && AreaOf(next) != FocusArea.Content)
        {
            if (direction == FocusNavigationDirection.Left)
            {
                EnterSidebar(repeat);
            }
            return true;
        }
        if (next is UIElement target)
        {
            target.Focus(FocusState.Keyboard);
        }
        return true;
    }

    // Radio buttons with a group name go together by it, those without by their panel.
    private static bool IsSameGroup(RadioButton radio, RadioButton other) =>
        string.IsNullOrEmpty(radio.GroupName)
            ? string.IsNullOrEmpty(other.GroupName) && ReferenceEquals(VisualTreeHelper.GetParent(radio), VisualTreeHelper.GetParent(other))
            : radio.GroupName == other.GroupName;

    private bool FocusSidebarEntry() =>
        Navigation.IsPaneVisible && Navigation.SelectedItem is NavigationViewItem entry && entry.Focus(FocusState.Keyboard);

    private void OnFocusMoved(object? sender, FocusManagerGotFocusEventArgs args)
    {
        if (args.NewFocusedElement is { } focused)
        {
            _startFocus.FocusMoved(focused);
        }
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
    // left from; a section picked in the sidebar keeps focus on its entry, as the TV app's tabs do.
    private async void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        bool back = e.NavigationMode == NavigationMode.Back && _backByKey;
        bool opened = e.NavigationMode == NavigationMode.New && _openedByKey;
        bool picked = opened && _openedFromSidebar;
        _backByKey = _openedByKey = _openedFromSidebar = false;
        RememberLeftPage(e.NavigationMode);
        var returning = _returning;
        _leaving = null;
        _returning = null;
        if (e.Content is not Page page || !(back || opened))
        {
            return;
        }
        // Taken now: the focus XAML gives the page as it comes in would replace it.
        if (!_pageFocus.TryGetValue(page, out var mark) && back && returning is not null)
        {
            _entryFocus.TryGetValue(returning, out mark);
        }
        bool IsSection() => opened && _navigation.IsAtSection;
        try
        {
            // Until the viewer presses a key or a mouse button; a section opened from elsewhere (Ctrl+K) places its own focus.
            var since = InputSoFar();
            await HoldFocusAsync(page, stops: () => MovedOnSince(since) || (IsSection() && !picked),
                place: () => IsSection() ? (FocusSidebarEntry() ? Navigation.SelectedItem as UIElement : null)
                    : back ? PlaceOnReturn(page, mark) : FocusArrival(page),
                fallback: () => FocusArrival(page) ?? FocusFirst(page));
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            _logger.LogError(ex, "Focus could not be placed on the page");
        }
    }

    // The start screen has gone: focus goes to the first page's start element (Home's first card, as the TV app starts on
    // its first row), quietly, so a mouse user sees no ring; the remote's first key shows it (ShowsStartFocus). After a key
    // pressed during the start it shows at once: the viewer uses keys already, and that key reached nothing. Not on a page
    // that places its own (sign-in, adding a server, the profiles), nor once anything else has focus or the viewer clicked
    // since the start began. A start that outlasted the start screen does it when its page comes.
    private void PlaceStartFocus(InputMark since)
    {
        if (ContentFrame.Content is not null || IsPlayerOpen)
        {
            FocusFirstPage(since);
            return;
        }
        ContentFrame.Navigated += OnFirstPage;

        void OnFirstPage(object sender, NavigationEventArgs e)
        {
            ContentFrame.Navigated -= OnFirstPage;
            FocusFirstPage(since);
        }
    }

    private async void FocusFirstPage(InputMark since)
    {
        if (ContentFrame.Content is not Page page || page is IOwnFocus || IsPlayerOpen || FocusedElement() is not null
            || ClickedSince(since))
        {
            return;
        }
        UIElement? placed = null;
        // Shown once a key has come since the start began, else quiet until the first key.
        UIElement? Place(Func<FocusState, UIElement?> focus)
        {
            bool shown = _keysPressed != since.Keys;
            placed = focus(shown ? FocusState.Keyboard : FocusState.Programmatic);
            if (placed is not null && !shown)
            {
                _startFocus.Place(placed);
            }
            return placed;
        }
        try
        {
            // A key that reached nothing does not end the wait; focus the viewer moved on from, or a click, does.
            await HoldFocusAsync(page,
                stops: () => ClickedSince(since) || (FocusedElement() is { } focused && !ReferenceEquals(focused, placed)),
                place: () => Place(state => FocusArrival(page, state)),
                fallback: () => Place(state => FocusArrival(page, state) ?? FocusFirst(page, state)));
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            _logger.LogError(ex, "Focus could not be placed on the first page");
        }
    }

    // Places focus on a page that has just come in once it shows its parts (a title loads, a grid makes its cards), and
    // holds the place against the focus XAML gives a page that comes in (the sidebar changing width moved it to the page's
    // first link). Stops when the page goes or stops says so (the viewer moved on); with nothing placed in time, fallback
    // places it.
    private async Task HoldFocusAsync(Page page, Func<bool> stops, Func<UIElement?> place, Func<UIElement?> fallback)
    {
        UIElement? placed = null;
        int held = 0;
        for (var waited = TimeSpan.Zero; waited < ArrivalPatience && held < HeldPolls; waited += ArrivalPoll)
        {
            await Task.Delay(ArrivalPoll);
            if (!ReferenceEquals(ContentFrame.Content, page) || stops())
            {
                return;
            }
            if (placed is not null && ReferenceEquals(FocusedElement(), placed))
            {
                held++;
                continue;
            }
            placed = place();
            held = 0;
        }
        if (placed is null && ReferenceEquals(ContentFrame.Content, page) && !stops())
        {
            _ = fallback();
        }
    }

    // The keys and mouse buttons the window has seen so far, to tell whether the viewer has moved on since.
    private InputMark InputSoFar() => new(_keysPressed, _pointersPressed);

    private bool MovedOnSince(InputMark since) => since != InputSoFar();

    private bool ClickedSince(InputMark since) => since.Presses != _pointersPressed;

    // A page left for a new one keeps its mark with its history entry, for when it is made again.
    private void RememberLeftPage(NavigationMode mode)
    {
        if (mode == NavigationMode.New && _leaving is { } left && ContentFrame.BackStack.LastOrDefault() is { } entry
            && entry.SourcePageType == left.GetType() && _pageFocus.TryGetValue(left, out var mark))
        {
            _entryFocus.AddOrUpdate(entry, mark);
        }
    }

    // Back to a page: where focus was on it. A page made again (a title's) finds the card it was left from in its lists;
    // left from anything else, it starts as when it opened, on its Play.
    private static UIElement? PlaceOnReturn(Page page, FocusMark? mark) =>
        mark is null || (mark.Card is null && !mark.IsOn(page)) ? FocusArrival(page) : RestoreFocus(page, mark);

    // The element focused, or null while the page has none to take it yet.
    private static UIElement? FocusArrival(Page page, FocusState state = FocusState.Keyboard)
    {
        if (page is not IArrivalFocus arrival)
        {
            return FocusFirst(page, state);
        }
        return arrival.ArrivalFocus is { IsLoaded: true, IsEnabled: true, Visibility: Visibility.Visible } target
            && target.Focus(state) ? target : null;
    }

    // The page's first card, as the TV app starts on the first row's first card; a page without cards on its first
    // element that takes focus.
    private static UIElement? FocusFirst(Page page, FocusState state = FocusState.Keyboard) =>
        (FirstCard(page) ?? FocusManager.FindFirstFocusableElement(page)) is UIElement first && first.Focus(state)
            ? first : null;

    // The player gave the window back (closed, or made the bar): focus returns to where it was on the page, as Back from a
    // title returns to its card; it left for the sidebar's first entry. Shown after a key, quiet after a click.
    private void ReturnFocusToPage()
    {
        if (ContentFrame.Content is not Page page)
        {
            return;
        }
        // Taken now: focus moved in the meantime would replace it.
        _pageFocus.TryGetValue(page, out var mark);
        var state = _lastInputWasKey ? FocusState.Keyboard : FocusState.Programmatic;
        int keys = _keysPressed;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // Not when the player closed for another page (a section picked from the bar), opened again, or the viewer
            // pressed a key meanwhile.
            if (IsPlayerFull || !ReferenceEquals(ContentFrame.Content, page) || _keysPressed != keys)
            {
                return;
            }
            _ = RestoreFocus(page, mark, state) ?? FocusFirst(page, state);
        });
    }

    // In the order the lists have their titles: a repeater's children are in the order it made them.
    private static Control? FirstCard(DependencyObject element)
    {
        if (element is UIElement { Visibility: Visibility.Collapsed })
        {
            return null;
        }
        if (element is Button { IsEnabled: true } card && IsCard(card))
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
    // title in its list, else by its place (ReturnTarget). A page made again on the way back (a title's) has new lists:
    // the title is looked for in each of them.
    private static UIElement? RestoreFocus(Page page, FocusMark? mark, FocusState state = FocusState.Keyboard)
    {
        if (mark is null)
        {
            return null;
        }
        if (mark.Element.TryGetTarget(out var element) && element.XamlRoot is not null && IsWithin(element, page)
            && (mark.Card is null || CardOf(element)?.Id == mark.Card.Id))
        {
            return element.Focus(state) ? element : null;
        }
        if (mark.Card is not { } card)
        {
            return null;
        }
        ItemsRepeater? repeater = null;
        mark.Repeater?.TryGetTarget(out repeater);
        var found = repeater is { XamlRoot: not null } && IsWithin(repeater, page)
            ? CardInList(repeater, card, mark.Index)
            : CardOnPage(page, card);
        if (found is null)
        {
            return null;
        }
        found.StartBringIntoView();
        return found.Focus(state) ? found : null;
    }

    // The card for the title in a list, else the one now at its place.
    private static Control? CardInList(ItemsRepeater repeater, MediaCard card, int previousIndex)
    {
        if (repeater.ItemsSourceView is not { Count: > 0 } items)
        {
            return null;
        }
        var cards = Enumerable.Range(0, items.Count).Select(i => CardOfItem(items.GetAt(i))).ToList();
        int index = ReturnTarget.IndexFor(cards, card, previousIndex, each => each?.Id);
        return index < 0 ? null : CardWithin(repeater.GetOrCreateElement(index));
    }

    // The card for the title in any of the page's lists, made if the list has not made it yet.
    private static Control? CardOnPage(DependencyObject element, MediaCard card)
    {
        if (element is UIElement { Visibility: Visibility.Collapsed })
        {
            return null;
        }
        if (element is ItemsRepeater { ItemsSourceView: { Count: > 0 } items } repeater)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (CardOfItem(items.GetAt(i))?.Id == card.Id)
                {
                    return CardWithin(repeater.GetOrCreateElement(i));
                }
            }
            return null;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (CardOnPage(VisualTreeHelper.GetChild(element, i), card) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    // A list's item may wrap its card in a plain Grid (see "ItemsRepeater templates" in AGENTS.md).
    private static Control? CardWithin(DependencyObject? element)
    {
        if (element is null || (element is Button button && IsCard(button)))
        {
            return element as Control;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (CardWithin(VisualTreeHelper.GetChild(element, i)) is { } found)
            {
                return found;
            }
        }
        return null;
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
        // Esc they take themselves.
        if (e.Key == VirtualKey.Escape || !BackKeys.IsBack(e.Key, Root.XamlRoot) || e.KeyStatus.WasKeyDown
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

    private bool IsTyping() => BackKeys.IsTyping(Root.XamlRoot);

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

    // How many keys and mouse buttons the window had seen at some moment.
    private readonly record struct InputMark(int Keys, int Presses);

    // Where focus was on a page; for a card, also the title it showed and its list and place, to find the title again.
    private sealed record FocusMark(WeakReference<UIElement> Element, MediaCard? Card, int Index, WeakReference<ItemsRepeater>? Repeater)
    {
        public bool IsOn(Page page) => Element.TryGetTarget(out var element) && element.XamlRoot is not null && IsWithin(element, page);

        public static FocusMark Of(UIElement element)
        {
            if (CardOf(element) is not { } card)
            {
                return new FocusMark(new(element), null, -1, null);
            }
            // The repeater knows the place of its own child, the item's root, which may hold the card.
            UIElement item = element;
            for (var parent = VisualTreeHelper.GetParent(item); parent is not null and not ItemsRepeater; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is UIElement ancestor)
                {
                    item = ancestor;
                }
            }
            return VisualTreeHelper.GetParent(item) is ItemsRepeater repeater
                ? new FocusMark(new(element), card, repeater.GetElementIndex(item), new(repeater))
                : new FocusMark(new(element), card, -1, null);
        }
    }
}
