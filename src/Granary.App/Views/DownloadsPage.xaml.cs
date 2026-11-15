using Granary.App.Controls;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>
/// The active profile's downloads on this PC (docs/ui-design.md, "Downloads"). The page is cached; it follows the downloads
/// only while it shows: attached as it is shown, detached as it is left, and as it unloads, so the downloads, which live as
/// long as the app, never hold it (<c>DownloadsViewModelTests</c>). Its cards' ⋮ menus get their entries from it. A
/// controller's bumpers step through its sections, Films and Series (<see cref="IPadSections"/>).
/// </summary>
public sealed partial class DownloadsPage : Page, ICardMenuHost, ICardFocusHost, IPadSections
{
    public DownloadsPage()
    {
        // Made outside the container: the root provider would keep the disposable view model, and the page with it, for good.
        ViewModel = ActivatorUtilities.CreateInstance<DownloadsViewModel>(App.Services);
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public DownloadsViewModel ViewModel { get; }

    public string EmptyTitle => DownloadsViewModel.EmptyTitle;

    public string EmptyText => DownloadsViewModel.EmptyText;

    public string OfflineNote => DownloadsViewModel.OfflineNote;

    public string FilmsTitle => DownloadsViewModel.FilmsTitle;

    public string SeriesTitle => DownloadsViewModel.SeriesTitle;

    /// <summary>Play, Pause, Resume, Retry, More Info and Delete download in the cards' menus.</summary>
    public ICardMenuSource CardMenu => ViewModel.Actions;

    /// <summary>
    /// The last film went with the focus: the first series takes it; the last series: the last film; neither is left: Reclaim
    /// Watched, when it shows. Whatever is left, a keyboard, a controller or a remote carries on from there.
    /// </summary>
    public void FocusAfterLastCard(MediaGrid grid, FocusState state)
    {
        bool moved = ReferenceEquals(grid, FilmsGrid) ? SeriesGrid.FocusCardAt(0, state)
            : ReferenceEquals(grid, SeriesGrid) && FilmsGrid.FocusCardAt(int.MaxValue, state);
        if (!moved && ReclaimButton.Visibility == Visibility.Visible)
        {
            ReclaimButton.Focus(state);
        }
    }

    /// <summary>
    /// A controller's bumpers: the page has no tabs, but two sections, Films and Series, so a bumper goes to the first card of
    /// the one before or after the section focus is in, as a bumper picks the tab beside the chosen one, and stops at the ends.
    /// From above them (Retry, Reclaim Watched) or the sidebar, RB goes to the first section with cards. A section without
    /// cards is stepped over.
    /// </summary>
    void IPadSections.StepSection(int by)
    {
        MediaGrid[] sections = [FilmsGrid, SeriesGrid];
        var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        int at = focused is null ? -1 : Array.FindIndex(sections, grid => MediaGrid.IsWithin(focused, grid));
        if (at < 0 && by < 0)
        {
            return;
        }
        for (int next = at + by; next >= 0 && next < sections.Length; next += by)
        {
            if (sections[next].FocusCardAt(0, FocusState.Keyboard))
            {
                return;
            }
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Detach();
        base.OnNavigatedFrom(e);
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
