using Granary.App.Controls;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>
/// A series' downloads on this PC (docs/ui-design.md, "Downloads"); the parameter is the series' id. Cached, and following the
/// downloads only while it shows, as the Downloads page does (<c>DownloadedSeriesViewModelTests</c>). Its cards' ⋮ menus get
/// their entries from it.
/// </summary>
public sealed partial class DownloadedSeriesPage : Page, ICardMenuHost
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

    // Also when the page goes without being navigated from. One shown again before its Unloaded came stays attached.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(Frame?.Content, this))
        {
            ViewModel.Detach();
        }
    }
}
