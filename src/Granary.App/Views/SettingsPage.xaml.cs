using Granary.App.Views.Settings;
using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>
/// The settings (docs/ui-design.md, Settings): the profile's, and the app's own servers and About. The parameter names the
/// page to open first (<c>playback</c>, <c>subtitles</c>, <c>servers</c>, <c>about</c>).
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly Dictionary<string, UIElement> _views = [];
    // Set while one list clears the other's pick, so the clearing opens nothing.
    private bool _switchingLists;

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
        if (AppPages.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag as string == page) is { } appPage)
        {
            AppPages.SelectedItem = appPage;
        }
        else
        {
            Pages.SelectedItem = Pages.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag as string == page) ?? Pages.Items[0];
        }
    }

    // A speed test left running would split the link with whatever plays next.
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        foreach (var view in _views.Values.OfType<PlaybackSettingsView>())
        {
            view.ViewModel.StopSpeedTest();
        }
        // The servers and the app's updates outlive the page; its views let go of them.
        foreach (var view in _views.Values.OfType<IDisposable>())
        {
            view.Dispose();
        }
        _views.Clear();
    }

    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingLists || sender is not ListView { SelectedItem: ListViewItem { Tag: string page } } list)
        {
            return;
        }
        _switchingLists = true;
        (ReferenceEquals(list, Pages) ? AppPages : Pages).SelectedItem = null;
        _switchingLists = false;
        if (!_views.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "subtitles" => new LanguageSettingsView(),
                "servers" => new ServersSettingsView(),
                "about" => new AboutSettingsView(),
                _ => new PlaybackSettingsView(),
            };
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
