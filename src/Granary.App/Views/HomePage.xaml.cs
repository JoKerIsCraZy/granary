using Granary.App.Controls;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

public sealed partial class HomePage : Page, IStartupContent
{
    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
        // The rows' view begins at the compact header, and the rows a fold's length below it, so at rest they start under
        // the full hero and scroll up under the header as it folds.
        Scroller.Margin = new Thickness(0, HomeHero.CompactHeight, 0, 0);
        Column.Padding = new Thickness(48, HomeHero.CollapseDistance, 48, 48);
        Loaded += (_, _) => Hero.FollowScroll(Scroller);
    }

    public HomeViewModel ViewModel { get; }

    /// <summary>The latest visit's load of the rows: the start screen waits for the first.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // The page is cached: Back keeps the scroll position, a new visit starts at the top.
        if (e.NavigationMode != NavigationMode.Back)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        ViewModel.DisplayScale = XamlRoot?.RasterizationScale ?? 1;
        try
        {
            var activation = ViewModel.ActivateAsync();
            Ready = activation;
            await activation;
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the view model.
            App.Services.GetRequiredService<ILogger<HomePage>>().LogError(ex, "Home could not be shown");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public Style StatusStyle(bool hasError) =>
        (Style)Application.Current.Resources[hasError ? "SiloBodyTextStyle" : "SiloRowTitleTextStyle"];
}
