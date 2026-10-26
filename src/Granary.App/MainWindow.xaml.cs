using Granary.App.Services;
using Granary.App.Views;
using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace Granary.App;

public sealed partial class MainWindow : Window
{
    private const string LibraryTagPrefix = "Library:";
    private const string ListTagPrefix = "List:";

    // How long a closing window waits for the player's stop; the stop itself keeps trying for longer.
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(4);

    private readonly NavigationService _navigation;
    private readonly PlaybackStops _stops;
    private readonly ILogger<MainWindow> _logger;
    private bool _closeAfterStops;

    public MainWindow(NavigationService navigation, ShellViewModel shell, SidebarViewModel sidebar, SidebarSections sections,
        NotificationBadge notifications, RequestsFeature requests, PlaybackStops stops, ILogger<MainWindow> logger)
    {
        _navigation = navigation;
        _stops = stops;
        _logger = logger;
        Shell = shell;
        Sidebar = sidebar;
        Sections = sections;
        Notifications = notifications;
        Requests = requests;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        StyleCaptionButtons();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));
        AppWindow.Closing += OnClosing;

        _navigation.Attach(ContentFrame);
        _navigation.Navigated += OnNavigated;
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.IsSignedIn))
            {
                ShowChrome(_navigation.Current);
            }
        };
        Sidebar.Libraries.CollectionChanged += (_, _) => ShowLibraries();
        Sections.PropertyChanged += (_, _) => ShowSections();
        Requests.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RequestsFeature.IsEnabled))
            {
                ShowSections();
            }
        };
        Notifications.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NotificationBadge.IsAvailable))
            {
                ShowSections();
            }
        };
        ShowLibraries();
    }

    /// <summary>Routes by session state; the app starts it once the window is up.</summary>
    public ShellViewModel Shell { get; }

    public SidebarViewModel Sidebar { get; }

    /// <summary>Which sidebar sections are folded away.</summary>
    public SidebarSections Sections { get; }

    // The fold buttons' parameters.
    public SidebarSection LibrariesSection => SidebarSection.Libraries;

    public SidebarSection DiscoverSection => SidebarSection.Discover;

    public SidebarSection YourStuffSection => SidebarSection.YourStuff;

    /// <summary>The sidebar's Notifications entry and its unread count.</summary>
    public NotificationBadge Notifications { get; }

    /// <summary>Whether the sidebar shows Requests.</summary>
    public RequestsFeature Requests { get; }

    // Instance method because x:Bind function bindings call it on the window.
    public string ProfileMenuName(string profileName) => $"Profile menu: {profileName}";

    public string NotificationsName(int unread) => unread > 0 ? $"Notifications, {unread} unread" : "Notifications";

    // The library entries follow the header; NavigationView has no item template for mixed static and bound items.
    private void ShowLibraries()
    {
        var items = Navigation.MenuItems;
        foreach (var stale in items.OfType<NavigationViewItem>().Where(IsLibraryItem).ToList())
        {
            items.Remove(stale);
        }
        int index = items.IndexOf(LibrariesHeader) + 1;
        foreach (var library in Sidebar.Libraries)
        {
            items.Insert(index++, new NavigationViewItem
            {
                Content = library.Name,
                Tag = LibraryTagPrefix + library.Id,
                Icon = new FontIcon { Glyph = LibraryGlyph(library.Type) },
            });
        }
        ShowLibrariesHeader();
        ShowSections();
        if (_navigation.Current is { } route)
        {
            SelectItem(route, _navigation.CurrentParameter);
        }
    }

    // A folded section hides its entries, all but the page shown, so the sidebar still says where the viewer is. The
    // icon-only pane shows them all, as the web's rail does: it has no headers to open them by. Requests and
    // Notifications show only while the server offers them.
    private void ShowSections()
    {
        bool open = Navigation.IsPaneOpen;
        bool shown = true;
        foreach (object item in Navigation.MenuItems)
        {
            if (ReferenceEquals(item, LibrariesHeader))
            {
                shown = Sections.IsLibrariesExpanded || !open;
            }
            else if (ReferenceEquals(item, DiscoverHeader))
            {
                shown = Sections.IsDiscoverExpanded || !open;
            }
            else if (ReferenceEquals(item, YourStuffHeader))
            {
                shown = Sections.IsYourStuffExpanded || !open;
            }
            else if (item is NavigationViewItem entry)
            {
                bool current = ReferenceEquals(entry, Navigation.SelectedItem);
                entry.Visibility = (shown || current) && IsOffered(entry) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private bool IsOffered(NavigationViewItem item) => item.Tag switch
    {
        nameof(AppRoute.Requests) => Requests.IsEnabled,
        nameof(AppRoute.Notifications) => Notifications.IsAvailable,
        _ => true,
    };

    private static bool IsLibraryItem(NavigationViewItem item) =>
        item.Tag is string tag && tag.StartsWith(LibraryTagPrefix, StringComparison.Ordinal);

    // Library types are free text (docs/api-contract.md, 7.3).
    private static string LibraryGlyph(string type) => type.ToLowerInvariant() switch
    {
        "movies" or "movie" or "films" => "",
        "series" or "tv" or "shows" or "tvshows" => "",
        "music" => "",
        _ => "",
    };

    private void StyleCaptionButtons()
    {
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Color.FromArgb(0xFF, 0xE8, 0xE8, 0xEC);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x7C, 0x7C, 0x86);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonPressedForegroundColor = Colors.White;
    }

    private void OnNavigated(object? sender, AppRoute route)
    {
        // A disabled arrow on pages without history is only noise; the player has its own back button.
        Navigation.IsBackEnabled = _navigation.CanGoBack;
        Navigation.IsBackButtonVisible = _navigation.CanGoBack && route is not (AppRoute.Player or AppRoute.Settings)
            ? NavigationViewBackButtonVisible.Visible
            : NavigationViewBackButtonVisible.Collapsed;
        ShowChrome(route);
        SelectItem(route, _navigation.CurrentParameter);
        RefreshNotifications();
    }

    // The unread count is read again on the way through the app, at most every half minute, as the web's sidebar does;
    // whether the server takes requests once per profile.
    private async void RefreshNotifications()
    {
        try
        {
            await Task.WhenAll(Notifications.RefreshAsync(), Requests.RefreshAsync());
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. Failed reads are already logged; this is anything else.
            _logger.LogError(ex, "The unread notification count could not be shown");
        }
    }

    // The sidebar is there for a signed-in profile. Detail pages get the full width, as on the web: the sidebar
    // shrinks to its icons. The player owns the whole window: no sidebar at all, and the page reaches under the
    // title bar, where the caption buttons float over the video. While the pane is hidden it must stay closed:
    // an open hidden pane still takes its width, empty. The player also puts the pane in minimal mode, where a
    // closed pane takes no width: hidden alone, a pane still closing when the player opened (played right after a
    // detail page came up from the home page) ended its animation as a 48 px strip beside the video.
    private void ShowChrome(AppRoute? route)
    {
        bool isPlayer = route == AppRoute.Player;
        // The settings bring their own sidebar, as on the web.
        bool hidesPane = isPlayer || route == AppRoute.Settings;
        Navigation.PaneDisplayMode = hidesPane ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.Left;
        // Visibility first: a pane that becomes visible opens again, whatever IsPaneOpen said before.
        Navigation.IsPaneVisible = Shell.IsSignedIn && !hidesPane;
        Navigation.IsPaneOpen = Shell.IsSignedIn && route is not (AppRoute.Item or AppRoute.Player);
        Grid.SetRow(Navigation, isPlayer ? 0 : 1);
        Grid.SetRowSpan(Navigation, isPlayer ? 2 : 1);
        AppTitle.Visibility = isPlayer ? Visibility.Collapsed : Visibility.Visible;
        ShowLibrariesHeader();
        ShowSections();
    }

    // "LIBRARIES" only in the open pane. NavigationView hides the header's text in the icon-only pane itself, but not
    // after the player's minimal mode: back in Left mode with the pane closed it showed again, cut off.
    private void ShowLibrariesHeader()
    {
        LibrariesHeader.Visibility = Sidebar.Libraries.Count > 0 && Navigation.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        var open = Navigation.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        DiscoverHeader.Visibility = open;
        YourStuffHeader.Visibility = open;
    }

    // The process ends with the window, and the player's stop carries where the viewer left off: the window goes
    // out of sight at once but closes only once the stop is through, or after a few seconds.
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeAfterStops)
        {
            return;
        }
        if (ContentFrame.Content is PlayerPage player)
        {
            player.ViewModel.Leave();
        }
        var stopped = _stops.WhenStoppedAsync(StopBudget);
        if (stopped.IsCompleted)
        {
            return;
        }
        args.Cancel = true;
        _closeAfterStops = true;
        sender.Hide();
        await stopped;
        Close();
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (ReferenceEquals(args.InvokedItemContainer, ProfileItem))
        {
            FlyoutBase.ShowAttachedFlyout(ProfileItem);
            return;
        }
        switch (args.InvokedItemContainer?.Tag)
        {
            case string tag when tag.StartsWith(LibraryTagPrefix, StringComparison.Ordinal):
                _navigation.ShowSection(AppRoute.Library, tag[LibraryTagPrefix.Length..]);
                break;
            case string tag when tag.StartsWith(ListTagPrefix, StringComparison.Ordinal):
                _navigation.ShowSection(AppRoute.List, tag[ListTagPrefix.Length..]);
                break;
            case string tag when Enum.TryParse<AppRoute>(tag, out var route)
                && route is AppRoute.Home or AppRoute.Search or AppRoute.Collections or AppRoute.ForYou or AppRoute.Calendar or AppRoute.Notifications
                    or AppRoute.Requests:
                _navigation.ShowSection(route);
                break;
            // Settings and Servers open over the page they were opened from, and go back to it.
            case string tag when Enum.TryParse<AppRoute>(tag, out var route):
                _navigation.NavigateTo(route);
                break;
        }
    }

    private void SelectItem(AppRoute route, object? parameter)
    {
        string tag = route switch
        {
            AppRoute.Library => LibraryTagPrefix + parameter,
            AppRoute.List => ListTagPrefix + parameter,
            // A collection opens from the Collections page, which stays picked.
            AppRoute.Collection => nameof(AppRoute.Collections),
            AppRoute.RecommendationSection => nameof(AppRoute.ForYou),
            // A title from TMDB, a Discover row and a studio's titles open from the Requests hub.
            AppRoute.Title or AppRoute.RequestTitles => nameof(AppRoute.Requests),
            _ => route.ToString(),
        };
        var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>()
            .FirstOrDefault(i => i.Tag as string == tag);
        // Pages without a sidebar entry (details, "Explore all") select nothing.
        if (!ReferenceEquals(Navigation.SelectedItem, item))
        {
            Navigation.SelectedItem = item;
            ShowSections();
        }
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => _navigation.GoBack();

    private void OnBackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = _navigation.CanGoBack;
        _navigation.GoBack();
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(Root).Properties.PointerUpdateKind == PointerUpdateKind.XButton1Pressed)
        {
            e.Handled = true;
            _navigation.GoBack();
        }
    }

    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Shell.IsSignedIn)
        {
            args.Handled = true;
            _navigation.ShowSection(AppRoute.Search);
        }
    }
}
