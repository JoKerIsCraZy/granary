using Granary.App.Views.Settings;
using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>The profile's settings (docs/ui-design.md, Settings); the parameter names the page to open first.</summary>
public sealed partial class SettingsPage : Page
{
    private readonly Dictionary<string, UIElement> _views = [];

    public SettingsPage()
    {
        Sidebar = App.Services.GetRequiredService<SidebarViewModel>();
        InitializeComponent();
    }

    /// <summary>The profile the settings belong to, as the app's sidebar names it.</summary>
    public SidebarViewModel Sidebar { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        string page = e.Parameter as string ?? "playback";
        Pages.SelectedItem = Pages.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag as string == page) ?? Pages.Items[0];
    }

    // A speed test left running would split the link with whatever plays next.
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        foreach (var view in _views.Values.OfType<PlaybackSettingsView>())
        {
            view.ViewModel.StopSpeedTest();
        }
    }

    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Pages.SelectedItem is not ListViewItem { Tag: string page })
        {
            return;
        }
        if (!_views.TryGetValue(page, out var view))
        {
            view = page == "subtitles" ? new LanguageSettingsView() : new PlaybackSettingsView();
            _views[page] = view;
        }
        PageHost.Content = view;
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    // Back to where the settings were opened from, or Home.
    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        var navigation = App.Services.GetRequiredService<INavigationService>();
        if (navigation.CanGoBack)
        {
            navigation.GoBack();
        }
        else
        {
            navigation.NavigateTo(AppRoute.Home);
        }
    }
}
