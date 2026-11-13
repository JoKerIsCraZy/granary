using Granary.App.Controls;
using Granary.App.Views;
using Granary.Core.Abstractions;
using Granary.Core.Controller;
using Granary.Core.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Granary.App;

// A game controller on the pages, the minimized player's bar among them: each button goes the way the remote's key for
// it goes (MainWindow.Remote.cs), so focus stops, crosses into the sidebar and comes back to its card just the same.
public sealed partial class MainWindow
{
    // A page's worth of the trigger: how much of what shows scrolls by, and how many steps of focus that may take.
    private const double PageShare = 0.8;
    private const int PageSteps = 12;

    // The page's scroll viewer the right stick last moved, and the element that had focus then: looked for once, not for
    // every reading while the stick is held.
    private const long ScrollerLookEvery = 500;
    private WeakReference<ScrollViewer>? _padScroller;
    private WeakReference<object>? _padScrollerFor;
    private long _padScrollerLooked;

    private void TakePadOnPages(PadAction action)
    {
        switch (action.Kind)
        {
            case PadActionKind.MoveFocus:
                MovePadFocus(PadControls.WayOf(action.Direction), action.IsRepeat);
                break;
            case PadActionKind.Activate:
                PressPadFocus();
                break;
            case PadActionKind.Back:
                Back(BackKey.BrowserBack);
                break;
            case PadActionKind.ContextMenu:
                ShowsCardMenu();
                break;
            case PadActionKind.Search when Shell.IsSignedIn:
                _navigation.ShowSection(AppRoute.Search);
                break;
            case PadActionKind.PlayFocused:
                PlayPadFocus();
                break;
            case PadActionKind.ToggleSidebar:
                TogglePadSidebar();
                break;
            case PadActionKind.PreviousTab:
                StepPadTab(-1);
                break;
            case PadActionKind.NextTab:
                StepPadTab(1);
                break;
            case PadActionKind.PageUp:
                PagePad(FocusNavigationDirection.Up);
                break;
            case PadActionKind.PageDown:
                PagePad(FocusNavigationDirection.Down);
                break;
            case PadActionKind.Scroll:
                ScrollPad(action.X, action.Y);
                break;
        }
    }

    // The D-pad or the left stick, as an arrow key: first what the window does with an arrow before XAML (the bar, a row's
    // end, a row of tabs), then the control's own use of it (a slider), then one step of focus within the page or the
    // sidebar, and at their edges what the window does with an arrow nobody took (into the sidebar and out of it).
    private void MovePadFocus(FocusNavigationDirection way, bool repeat)
    {
        if (ShowsStartFocus(isArrowOrOk: true))
        {
            return;
        }
        if (FocusedElement() is not { } focused)
        {
            PlacePadFocus();
            return;
        }
        if (TakesArrowFirst(way, repeat) || PadControls.Nudge(focused, way))
        {
            return;
        }
        DependencyObject? scope = AreaOf(focused) switch
        {
            FocusArea.Content => ContentFrame,
            FocusArea.Sidebar => Navigation,
            // The way back and the title bar's buttons lie outside both.
            _ => Root.XamlRoot?.Content,
        };
        if (scope is null || !PadControls.Move(way, scope))
        {
            TakesArrowLast(way, repeat);
        }
        // The rail a pad opened closes once focus has left it, as it does for a mouse that left.
        if (_isRail && Navigation.IsPaneOpen && AreaOf(FocusedElement()) != FocusArea.Sidebar)
        {
            CloseRail();
        }
    }

    // Nothing has focus (a tab's cards went with it, a click on the page's ground): the pad starts on the page's first card.
    private void PlacePadFocus()
    {
        if (ContentFrame.Content is not Page page || FocusFirst(page) is null)
        {
            FocusSidebarEntry();
        }
    }

    private void PressPadFocus()
    {
        if (ShowsStartFocus(isArrowOrOk: true))
        {
            return;
        }
        if (FocusedElement() is { } focused)
        {
            PadControls.Press(focused);
        }
        else
        {
            PlacePadFocus();
        }
    }

    // X on a card plays it, as a remote's Play key; while the player is the bar it plays or pauses that, as the key does
    // through Windows' media session.
    private void PlayPadFocus()
    {
        if (!PlayFocused() && _player is { IsDocked: true } docked)
        {
            docked.ViewModel.TogglePauseCommand.Execute(null);
        }
    }

    // View goes to the sidebar and back: to the section's entry, and from there to where focus was on the page. Beside a
    // title's page the sidebar is its rail, which opens for it and closes again.
    private void TogglePadSidebar()
    {
        if (!Navigation.IsPaneVisible)
        {
            return;
        }
        if (AreaOf(FocusedElement()) == FocusArea.Sidebar)
        {
            LeaveSidebar(repeat: false);
            CloseRail();
            return;
        }
        OpenRail();
        if (!FocusSidebarEntry() && FocusManager.FindFirstFocusableElement(Navigation) is { } first)
        {
            PadControls.Focus(first);
        }
    }

    // A bumper goes to the tab or pill before or after the chosen one, and focus with it, so the viewer sees where they
    // are: the row of tabs focus is on, else the page's own sections (Settings' and Admin's pages, Search's Movies and
    // Series; IPadSections), else the page's first row of tabs (a library's Recommended, Library and Collections). It
    // stops at the ends. A page without any has its row of cards paged instead.
    private void StepPadTab(int by)
    {
        if (ContentFrame.Content is not Page page)
        {
            return;
        }
        var tabs = new List<RadioButton>();
        CollectTabs(page, tabs);
        var chosen = FocusedElement() as RadioButton is { } focused && tabs.Contains(focused) ? focused : null;
        if (chosen is null && page is IPadSections sections)
        {
            sections.StepSection(by);
            return;
        }
        chosen ??= tabs.FirstOrDefault(tab => tab.IsChecked == true && tabs.Count(other => IsSameGroup(tab, other)) > 1);
        if (chosen is null)
        {
            PagePadRow(by);
            return;
        }
        var group = tabs.Where(tab => IsSameGroup(chosen, tab)).ToList();
        int index = group.FindIndex(tab => tab.IsChecked == true) is var at and >= 0 ? at : group.IndexOf(chosen);
        if (index + by >= 0 && index + by < group.Count)
        {
            var next = group[index + by];
            next.IsChecked = true;
            next.Focus(FocusState.Keyboard);
        }
    }

    // The radio buttons that show, in the order they are drawn.
    private static void CollectTabs(DependencyObject element, List<RadioButton> tabs)
    {
        if (element is UIElement { Visibility: Visibility.Collapsed })
        {
            return;
        }
        if (element is RadioButton { IsEnabled: true } tab)
        {
            tabs.Add(tab);
            return;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            CollectTabs(VisualTreeHelper.GetChild(element, index), tabs);
        }
    }

    // A bumper on a card of a sideways row moves on by as many cards as show, less one, so the last one seen stays in view.
    private void PagePadRow(int by)
    {
        if (FocusedElement() is not Button card || !IsCard(card)
            || FindAncestorOrSelf<ItemsRepeater>(card) is not { Layout: StackLayout { Orientation: Orientation.Horizontal } layout, ItemsSourceView: { } items } row)
        {
            return;
        }
        UIElement item = card;
        while (VisualTreeHelper.GetParent(item) is UIElement parent && !ReferenceEquals(parent, row))
        {
            item = parent;
        }
        int index = row.GetElementIndex(item);
        double shown = FindAncestorOrSelf<ScrollViewer>(row)?.ViewportWidth ?? row.ActualWidth;
        int cards = Math.Max(1, (int)(shown / Math.Max(1, card.ActualWidth + layout.Spacing)) - 1);
        int target = Math.Clamp(index + (by * cards), 0, items.Count - 1);
        if (index < 0 || target == index || CardWithin(row.GetOrCreateElement(target)) is not { } next)
        {
            return;
        }
        next.StartBringIntoView();
        next.Focus(FocusState.Keyboard);
    }

    // A trigger pages: focus goes on, row by row, until most of what showed has gone by, and the page follows it. Where
    // nothing can take focus (a long text), and where nothing more lies that way (a long text under the page's last
    // button), the page itself scrolls and focus stays.
    private void PagePad(FocusNavigationDirection way)
    {
        var focused = FocusedElement();
        if (ScrollerFor(focused) is not { } scroller)
        {
            return;
        }
        if (focused is not UIElement from || !IsWithin(from, scroller) || scroller.Content is not UIElement content
            || ScopeOf(from) is not { } scope)
        {
            ScrollPage(scroller, way);
            return;
        }
        double start = TopIn(from, content);
        int steps = 0;
        while (steps < PageSteps && PadControls.Move(way, scope))
        {
            steps++;
            if (FocusedElement() is not UIElement now || !IsWithin(now, scroller)
                || Math.Abs(TopIn(now, content) - start) >= scroller.ViewportHeight * PageShare)
            {
                break;
            }
        }
        if (steps == 0)
        {
            ScrollPage(scroller, way);
        }
    }

    private static void ScrollPage(ScrollViewer scroller, FocusNavigationDirection way)
    {
        double by = scroller.ViewportHeight * PageShare * (way == FocusNavigationDirection.Down ? 1 : -1);
        scroller.ChangeView(null, scroller.VerticalOffset + by, null);
    }

    private static double TopIn(UIElement element, UIElement content) => element.TransformToVisual(content).TransformPoint(default).Y;

    // What a focus step stays within: an open menu or dialog, the page, or the sidebar.
    private DependencyObject? ScopeOf(DependencyObject focused) =>
        OpenPopup() is { Child: { } child } && IsWithin(focused, child) ? child
        : AreaOf(focused) switch
        {
            FocusArea.Content => ContentFrame,
            FocusArea.Sidebar => Navigation,
            _ => null,
        };

    // The right stick scrolls what the viewer looks at, and focus stays where it is.
    private void ScrollPad(double right, double up)
    {
        if (ScrollerFor(FocusedElement()) is not { } scroller)
        {
            return;
        }
        double? across = scroller.ScrollableWidth > 0 && right != 0 ? scroller.HorizontalOffset + right : null;
        scroller.ChangeView(across, scroller.VerticalOffset - up, null, disableAnimation: true);
    }

    // The scroll viewer around what has focus that can scroll up and down; with focus elsewhere (in the sidebar, nowhere)
    // the first such one of the open menu or of the page.
    private ScrollViewer? ScrollerFor(DependencyObject? focused)
    {
        object lookedFor = focused is null ? this : focused;
        long now = Environment.TickCount64;
        if (_padScrollerFor is not null && _padScrollerFor.TryGetTarget(out var keptFor) && ReferenceEquals(keptFor, lookedFor))
        {
            if (_padScroller is not null && _padScroller.TryGetTarget(out var kept) && kept is { IsLoaded: true, ScrollableHeight: > 0 })
            {
                return kept;
            }
            // Nothing to scroll a moment ago: not looked for again with every reading.
            if (_padScroller is null && now - _padScrollerLooked < ScrollerLookEvery)
            {
                return null;
            }
        }
        DependencyObject? within = OpenPopup()?.Child ?? ContentFrame.Content as DependencyObject;
        var found = FindAncestorOrSelf<ScrollViewer>(focused, viewer => viewer.ScrollableHeight > 0)
            ?? (within is null ? null : FirstScroller(within));
        _padScroller = found is null ? null : new(found);
        _padScrollerFor = new(lookedFor);
        _padScrollerLooked = now;
        return found;
    }

    private static ScrollViewer? FirstScroller(DependencyObject element)
    {
        if (element is UIElement { Visibility: Visibility.Collapsed })
        {
            return null;
        }
        if (element is ScrollViewer { ScrollableHeight: > 0 } viewer)
        {
            return viewer;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            if (FirstScroller(VisualTreeHelper.GetChild(element, index)) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
