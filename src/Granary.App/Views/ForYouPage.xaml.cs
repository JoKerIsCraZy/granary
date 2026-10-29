using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>The profile's recommendation rows, under the hero of the card in focus.</summary>
public sealed partial class ForYouPage : Page
{
    public ForYouPage()
    {
        ViewModel = App.Services.GetRequiredService<ForYouViewModel>();
        InitializeComponent();
        Loaded += (_, _) => Hero.FollowScroll(Scroller);
    }

    public ForYouViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.DisplayScale = XamlRoot?.RasterizationScale ?? 1;
        Hero.Resume();
        try
        {
            // Back keeps the rows as they were; the sidebar reads them again.
            await ViewModel.ActivateAsync(isReturn: e.NavigationMode == NavigationMode.Back);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the page.
            App.Services.GetRequiredService<ILogger<ForYouPage>>().LogError(ex, "The recommendations could not be shown");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // The page stays cached: its hero lets go of its pictures until it shows again.
        Hero.Suspend();
        base.OnNavigatedFrom(e);
    }
}
