using Granary.App.Views.Settings;
using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>
/// The settings (docs/ui-design.md, Settings): the profile's, the account's devices, and the app's own servers and About. The
/// parameter names the page to open first (<c>playback</c>, <c>subtitles</c>, <c>interface</c>, <c>devices</c>, <c>servers</c>,
/// <c>about</c>). The mpv.conf editor opens in Playback's place; leaving with unsaved edits in it asks first.
/// </summary>
public sealed partial class SettingsPage : Page, IDockSpace, ILeaveGuard
{
    private const string PlaybackPage = "playback";

    private readonly Dictionary<string, UIElement> _views = [];
    // Set while one list clears the others' picks, so the clearing opens nothing.
    private bool _switchingLists;

    // The mpv.conf editor while it is open, and whether the viewer let its unsaved edits go for a navigation.
    private MpvConfigEditorView? _editor;
    private bool _released;

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
        foreach (var list in Lists())
        {
            if (list.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag as string == page) is { } item)
            {
                list.SelectedItem = item;
                return;
            }
        }
        Pages.SelectedItem = Pages.Items[0];
    }

    // A speed test left running would split the link with whatever plays next.
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        foreach (var view in _views.Values.OfType<PlaybackSettingsView>())
        {
            view.ViewModel.StopSpeedTest();
            view.EditMpvConfigRequested -= OnEditMpvConfigRequested;
        }
        CloseEditor();
        // The servers and the app's updates outlive the page; its views let go of them.
        foreach (var view in _views.Values.OfType<IDisposable>())
        {
            view.Dispose();
        }
        _views.Clear();
    }

    private async void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingLists || sender is not ListView { SelectedItem: ListViewItem { Tag: string page } } list)
        {
            return;
        }
        try
        {
            // The editor stands in for Playback: another page closes it, asking first about unsaved edits.
            if (_editor is { } editor && !await editor.ViewModel.ConfirmDiscardAsync())
            {
                if (ReferenceEquals(_editor, editor))
                {
                    Select(Pages, PlaybackPage);
                }
                return;
            }
            CloseEditor();
            Select(list, page);
            Show(page);
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            App.Services.GetRequiredService<ILogger<SettingsPage>>().LogError(ex, "A settings page could not open");
        }
    }

    // One page is picked across all the lists.
    private void Select(ListView list, string page)
    {
        _switchingLists = true;
        try
        {
            foreach (var other in Lists().Where(other => !ReferenceEquals(other, list)))
            {
                other.SelectedItem = null;
            }
            list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag as string == page);
        }
        finally
        {
            _switchingLists = false;
        }
    }

    private void Show(string page)
    {
        if (!_views.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "subtitles" => new LanguageSettingsView(),
                "interface" => new InterfaceSettingsView(),
                "devices" => new DevicesSettingsView(),
                "servers" => new ServersSettingsView(),
                "about" => new AboutSettingsView(),
                _ => new PlaybackSettingsView(),
            };
            if (view is PlaybackSettingsView playback)
            {
                playback.EditMpvConfigRequested += OnEditMpvConfigRequested;
            }
            _views[page] = view;
        }
        PageHost.Content = view;
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    // The editor in the Playback page's place, from the top; the Playback page stays as it was for the way back.
    private void OnEditMpvConfigRequested(object? sender, EventArgs e)
    {
        if (_editor is not null)
        {
            return;
        }
        _editor = new MpvConfigEditorView();
        _editor.Closed += OnEditorClosed;
        PageHost.Content = _editor;
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void OnEditorClosed(object? sender, EventArgs e)
    {
        var state = _editor?.ClosedWith ?? FocusState.Programmatic;
        CloseEditor();
        if (_views.GetValueOrDefault(PlaybackPage) is PlaybackSettingsView playback)
        {
            PageHost.Content = playback;
            playback.FocusEditMpvConfig(state);
        }
    }

    private void CloseEditor()
    {
        if (_editor is { } editor)
        {
            editor.Closed -= OnEditorClosed;
            _editor = null;
        }
        _released = false;
    }

    bool ILeaveGuard.AsksBeforeLeaving => !_released && _editor?.ViewModel.HasUnsavedChanges == true;

    async Task<bool> ILeaveGuard.ConfirmLeaveAsync()
    {
        _released = _editor is not { } editor || await editor.ViewModel.ConfirmDiscardAsync();
        return _released;
    }

    void ILeaveGuard.Rearm() => _released = false;

    private ListView[] Lists() => [Pages, AppearancePages, AccountPages, AppPages];

    // The settings' sidebar runs on beside the minimized player's bar; only the page ends above it.
    void IDockSpace.ReserveBottom(double height) => PageScroller.Margin = new Thickness(0, 0, 0, height);

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
