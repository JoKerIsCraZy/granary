using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>The profile's own, shared and server collections.</summary>
public sealed partial class CollectionsPage : Page
{
    public CollectionsPage()
    {
        ViewModel = App.Services.GetRequiredService<CollectionsViewModel>();
        InitializeComponent();
        // The own collections' cards carry Edit, sharing and Delete in their menu.
        OwnGrid.CardMenu = ViewModel;
    }

    public CollectionsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            // Back from a collection keeps the page as it was; the sidebar loads it fresh.
            await ViewModel.ActivateAsync(isReturn: e.NavigationMode == NavigationMode.Back);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the page.
            App.Services.GetRequiredService<ILogger<CollectionsPage>>().LogError(ex, "The collections could not be shown");
        }
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public Visibility ServerSectionShown(bool isShown, bool hasError) => isShown || hasError ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A card's ⋯ entry's outcome: muted, or the web's destructive red when it failed.</summary>
    public Microsoft.UI.Xaml.Media.Brush NoticeBrush(bool failed) => failed
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44))
        : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SiloMutedBrush"];

    public Visibility HasPills(IReadOnlyList<SettingChoice> choices) => choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}
