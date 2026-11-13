using Granary.App.Controls;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>
/// The active profile's downloads on this PC (docs/ui-design.md, "Downloads"). The page is cached; it follows the downloads
/// only while it shows: attached as it is shown, detached as it is left, and as it unloads, so the downloads, which live as
/// long as the app, never hold it (<c>DownloadsViewModelTests</c>). Its cards' ⋮ menus get their entries from it.
/// </summary>
public sealed partial class DownloadsPage : Page, ICardMenuHost
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
