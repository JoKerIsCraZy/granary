using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

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
            await ViewModel.ActivateAsync();
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
