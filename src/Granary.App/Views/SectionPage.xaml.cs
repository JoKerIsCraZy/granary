using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>"Explore all" of a Home or library row; the parameter is a <see cref="SectionRouteParameter"/>.</summary>
public sealed partial class SectionPage : Page
{
    public SectionPage()
    {
        ViewModel = App.Services.GetRequiredService<SectionViewModel>();
        InitializeComponent();
    }

    public SectionViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not SectionRouteParameter section)
        {
            return;
        }
        // The page is cached: Back to the same row keeps its pages and scroll position.
        bool keeps = e.NavigationMode == NavigationMode.Back && ViewModel.Section == section;
        if (!keeps)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        ViewModel.DisplayScale = XamlRoot?.RasterizationScale ?? 1;
        try
        {
            await ViewModel.OpenAsync(section, reload: !keeps);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the view model.
            App.Services.GetRequiredService<ILogger<SectionPage>>().LogError(ex, "The row could not be shown");
        }
    }

    // Instance method because x:Bind function bindings call it on the page.
    public Visibility HasText(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
}
