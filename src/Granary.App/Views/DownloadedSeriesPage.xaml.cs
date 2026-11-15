using Granary.App.Controls;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>
/// A series' downloads on this PC (docs/ui-design.md, "Downloads"); the parameter is the series' id. Cached, and following the
/// downloads only while it shows, as the Downloads page does (<c>DownloadedSeriesViewModelTests</c>). Its cards' ⋮ menus get
/// their entries from it. A controller's bumpers step through its seasons (<see cref="IPadSections"/>).
/// </summary>
public sealed partial class DownloadedSeriesPage : Page, ICardMenuHost, IPadSections
{
    public DownloadedSeriesPage()
    {
        // Made outside the container: the root provider would keep the disposable view model, and the page with it, for good.
        ViewModel = ActivatorUtilities.CreateInstance<DownloadedSeriesViewModel>(App.Services);
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public DownloadedSeriesViewModel ViewModel { get; }

    /// <summary>The Downloads page's entries: Play, Pause, Resume, Retry, More Info and Delete download.</summary>
    public ICardMenuSource CardMenu => ViewModel.Actions;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string seriesId)
        {
            // Another series starts at the top; Back to the same one keeps where the viewer was.
            if (!string.Equals(seriesId, ViewModel.SeriesId, StringComparison.Ordinal))
            {
                Scroller.ChangeView(null, 0, null, disableAnimation: true);
            }
            ViewModel.Open(seriesId);
        }
        ViewModel.Attach();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Detach();
        base.OnNavigatedFrom(e);
    }

    // A season goes with its last episode. Had one of its cards the focus, the next season's first card takes it (the one
    // before, when it was the last), so a keyboard, a controller or a remote carries on through the page; with no season
    // left, the page goes back by itself. The season's place is the repeater's while it clears the season, which seasons
    // before it that came or went have moved.
    private void OnSeasonClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (XamlRoot is not { } root || FocusManager.GetFocusedElement(root) is not DependencyObject focused
            || !MediaGrid.IsWithin(focused, args.Element) || sender.GetElementIndex(args.Element) is not (>= 0 and var place))
        {
            return;
        }
        var state = focused is Control { FocusState: FocusState.Keyboard } ? FocusState.Keyboard : FocusState.Programmatic;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusSeasonAt(root, place, state));
    }

    private void FocusSeasonAt(XamlRoot root, int place, FocusState state)
    {
        int count = ViewModel.Seasons.Count;
        var focused = FocusManager.GetFocusedElement(root) as DependencyObject;
        bool lost = focused is null || focused is FrameworkElement { IsLoaded: false } || ReferenceEquals(focused, Scroller);
        if (!lost || count == 0 || !IsLoaded)
        {
            return;
        }
        if (FindGrid(SeasonsRepeater.GetOrCreateElement(Math.Clamp(place, 0, count - 1))) is { } grid)
        {
            grid.FocusCardAt(0, state);
        }
    }

    /// <summary>
    /// A controller's bumpers: the page's sections are its seasons ("Season 1", Specials last), so a bumper goes to the first
    /// card of the season before or after the one focus is in, as a bumper picks the tab beside the chosen one, and stops at
    /// the ends; from outside them (the Back disc, the sidebar) RB goes to the first season. The season comes into view first:
    /// the page makes a season's grid only once it nears the view.
    /// </summary>
    void IPadSections.StepSection(int by)
    {
        var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        int at = focused is null ? -1 : SeasonIndexOf(focused);
        int next = at + by;
        if ((at < 0 && by < 0) || next < 0 || next >= ViewModel.Seasons.Count)
        {
            return;
        }
        SeasonsRepeater.GetOrCreateElement(next).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        FocusSeasonCard(next, SeasonFocusTries);
    }

    // How many layout passes a bumper's season gets to come into view and make its grid.
    private const int SeasonFocusTries = 3;

    // The season's first card takes the focus; a season not laid out yet is asked for again after the next pass, a few
    // times, while the page still shows. The season is looked up each time: the repeater may have used its element for
    // another one meanwhile.
    private void FocusSeasonCard(int place, int tries)
    {
        if (!IsLoaded || !ReferenceEquals(Frame?.Content, this) || place >= ViewModel.Seasons.Count)
        {
            return;
        }
        if (FindGrid(SeasonsRepeater.GetOrCreateElement(place)) is { } grid && grid.FocusCardAt(0, FocusState.Keyboard))
        {
            return;
        }
        if (tries > 1)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusSeasonCard(place, tries - 1));
        }
    }

    // The place of the season an element is in; -1 outside the seasons.
    private int SeasonIndexOf(DependencyObject element)
    {
        for (var at = element; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement season && ReferenceEquals(VisualTreeHelper.GetParent(season), SeasonsRepeater))
            {
                return SeasonsRepeater.GetElementIndex(season);
            }
        }
        return -1;
    }

    private static MediaGrid? FindGrid(DependencyObject element)
    {
        if (element is MediaGrid grid)
        {
            return grid;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (FindGrid(VisualTreeHelper.GetChild(element, i)) is { } inside)
            {
                return inside;
            }
        }
        return null;
    }

    // Also when the page goes without being navigated from. One shown again before its Unloaded came stays attached.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(Frame?.Content, this))
        {
            ViewModel.Detach();
        }
    }
}
