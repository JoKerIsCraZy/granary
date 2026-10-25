using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>A For You row in full; the parameter is a <see cref="RecommendationSectionRouteParameter"/>.</summary>
public sealed partial class RecommendationSectionPage : Page
{
    public RecommendationSectionPage()
    {
        ViewModel = App.Services.GetRequiredService<RecommendationSectionViewModel>();
        InitializeComponent();
    }

    public RecommendationSectionViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.DisplayScale = XamlRoot?.RasterizationScale ?? 1;
        if (e.NavigationMode != NavigationMode.Back)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        try
        {
            await ViewModel.ShowAsync(e.Parameter as RecommendationSectionRouteParameter, isReturn: e.NavigationMode == NavigationMode.Back);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the page.
            App.Services.GetRequiredService<ILogger<RecommendationSectionPage>>().LogError(ex, "A recommendation section could not be shown");
        }
    }
}
