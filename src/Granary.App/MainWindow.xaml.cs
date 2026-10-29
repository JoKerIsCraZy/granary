using Granary.App.Controls;
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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Dispatching;
using Windows.UI;

namespace Granary.App;

public sealed partial class MainWindow : Window
{
    private const string LibraryTagPrefix = SidebarLayout.LibraryTagPrefix;
    private const string ListTagPrefix = "List:";

    // How long a closing window waits for the player's stop; the stop itself keeps trying for longer.
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(4);

    // The web's delay before the rail opens under the pointer, so passing over it opens nothing.
    private static readonly TimeSpan RailHoverDelay = TimeSpan.FromMilliseconds(150);

    // A page whose sidebar changes width shows once the pane stands, or after this at the latest, and fades in this fast.
    private static readonly TimeSpan RevealFallback = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan RevealFade = TimeSpan.FromMilliseconds(160);

    private readonly NavigationService _navigation;
    private readonly PlaybackStops _stops;
    private readonly Granary.Core.WatchParty.WatchPartySession _room;
    private readonly ILogger<MainWindow> _logger;
    private readonly DispatcherQueueTimer _railHover;
    private readonly DispatcherQueueTimer _reveal;
    private bool _revealPending;
    private bool _closeAfterStops;
    private bool _isRail;
    private bool _showsPageBack;

    // The start screen leaves once its intro has played and the first page has what it shows, or after this at the
    // latest, so a slow server or a failure does not keep the app hidden; it fades out this fast.
    private static readonly TimeSpan StartupLimit = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SplashFade = TimeSpan.FromMilliseconds(300);

    private readonly CancellationTokenSource _updatesRun = new();

    public MainWindow(NavigationService navigation, ShellViewModel shell, SidebarViewModel sidebar, SidebarSections sections,
        SidebarLayout layout, NotificationBadge notifications, RequestsFeature requests, PlaybackStops stops, AppUpdatesViewModel updates,
        Granary.Core.WatchParty.WatchPartyFeature watchParty, Granary.Core.WatchParty.WatchPartySession room, ILogger<MainWindow> logger)
    {
        Layout = layout;
        Updates = updates;
        _navigation = navigation;
        _stops = stops;
        _room = room;
        _logger = logger;
        Shell = shell;
        Sidebar = sidebar;
        Sections = sections;
        Notifications = notifications;
        Requests = requests;
        WatchParty = watchParty;
        InitializeComponent();
        _railHover = DispatcherQueue.CreateTimer();
        _railHover.Interval = RailHoverDelay;
        _railHover.IsRepeating = false;
        _railHover.Tick += (_, _) => OpenRail();
        _reveal = DispatcherQueue.CreateTimer();
        _reveal.Interval = RevealFallback;
        _reveal.IsRepeating = false;
        _reveal.Tick += (_, _) => RevealPage();
        Navigation.Loaded += (_, _) => FollowRailPointer();
        // A restart into an update closes the window as the user would; the update is handed over as it closes.
        Updates.CloseRequested += (_, _) => Close();
        Closed += (_, _) =>
        {
            _updatesRun.Cancel();
            Updates.OnExiting();
        };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        StyleCaptionButtons();
        // The exe carries the icon too (ApplicationIcon); an unpackaged window still has to be given it.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Logo", "granary.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));
        AppWindow.Closing += OnClosing;

        _navigation.Attach(ContentFrame);
        _navigation.AttachPlayer(this);
        _navigation.TransitionFor = TransitionFor;
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
        Layout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SidebarLayout.Revision))
            {
                ArrangeSections();
                ShowSections();
            }
        };
        Requests.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RequestsFeature.IsEnabled))
            {
                ShowSections();
            }
        };
        WatchParty.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Granary.Core.WatchParty.WatchPartyFeature.IsAvailable))
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

    /// <summary>The profile's hidden entries and their order (Settings → Navigation &amp; Cards → Sidebar).</summary>
    public SidebarLayout Layout { get; }

    // The fold buttons' parameters.
    public SidebarSection LibrariesSection => SidebarSection.Libraries;

    public SidebarSection DiscoverSection => SidebarSection.Discover;

    public SidebarSection YourStuffSection => SidebarSection.YourStuff;

    /// <summary>The sidebar's Notifications entry and its unread count.</summary>
    public NotificationBadge Notifications { get; }

    /// <summary>Whether the sidebar shows Requests.</summary>
    public RequestsFeature Requests { get; }

    /// <summary>Whether the sidebar shows Watch Party.</summary>
    public Granary.Core.WatchParty.WatchPartyFeature WatchParty { get; }

    /// <summary>Granary's own updates: the notice of one that is ready (docs/release.md).</summary>
    public AppUpdatesViewModel Updates { get; }

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
        ArrangeSections();
        ShowHeaders();
        ShowSections();
        if (_navigation.Current is { } route)
        {
            SelectItem(route, _navigation.CurrentParameter);
        }
    }

    // The icon-only rail of a detail page opens over the page while the mouse rests on it, as the web's does: the pane
    // is the NavigationView's SplitView pane, found once the template is up.
    private void FollowRailPointer()
    {
        if (FindDescendant<SplitView>(Navigation)?.Pane is not { } pane)
        {
            _logger.LogWarning("The sidebar's pane was not found; the rail does not open under the pointer");
            return;
        }
        // Handled ones too: a button under the mouse (a section's header) marks its own pointer events handled, and a mouse
        // leaving the pane over one left the pane open.
        pane.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, e) =>
        {
            if (_isRail && !Navigation.IsPaneOpen && e.Pointer.PointerDeviceType is PointerDeviceType.Mouse or PointerDeviceType.Pen)
            {
                _railHover.Start();
            }
        }), handledEventsToo: true);
        pane.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, e) =>
        {
            // PointerExited bubbles: the entries move as the pane opens (the server's name appears on top), and the one
            // the mouse was on reports it left. Only a mouse outside the pane as shown has left it, as the web's
            // mouseleave; reacting to the rest closed the pane under the mouse and opened it again, on and off.
            if (!IsOverPane(pane, e))
            {
                CloseRail();
            }
        }), handledEventsToo: true);
        // And whatever the pane heard: a mouse moving beside the open rail has left it.
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (_isRail && (Navigation.IsPaneOpen || _railHover.IsRunning) && !IsOverPane(pane, e))
            {
                CloseRail();
            }
        }), handledEventsToo: true);
    }

    private void CloseRail()
    {
        _railHover.Stop();
        if (_isRail && Navigation.IsPaneOpen)
        {
            Navigation.IsPaneOpen = false;
        }
    }

    // The pane is as wide as the open pane even as the rail, which shows only its first CompactPaneLength.
    private bool IsOverPane(UIElement pane, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(pane).Position;
        double width = Navigation.IsPaneOpen ? Navigation.OpenPaneLength : Navigation.CompactPaneLength;
        return point.X >= 0 && point.X < width && point.Y >= 0 && point.Y < pane.ActualSize.Y;
    }

    private void OpenRail()
    {
        if (_isRail)
        {
            Navigation.IsPaneOpen = true;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child as T ?? FindDescendant<T>(child)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Plays the mark's intro over the window until the shell has <paramref name="started"/> and its first page has what it
    /// shows; a longer wait runs the loading loop meanwhile, and <see cref="StartupLimit"/> ends it in any case.
    /// </summary>
    public async void ShowStartup(Task started)
    {
        try
        {
            var limit = Task.Delay(StartupLimit);
            var ready = StartupReadyAsync(started);
            await SplashMark.PlayIntroAsync();
            if (!ready.IsCompleted && !limit.IsCompleted)
            {
                SplashMark.IsLoading = true;
            }
            await Task.WhenAny(ready, limit);
        }
        catch (Exception ex)
        {
            // Never the start's to fail: the app shows at once.
            _logger.LogWarning(ex, "The start screen ended early");
        }
        HideSplash();
        // Updates are looked for once the app shows, so the first page's requests go first.
        await Updates.RunAsync(_updatesRun.Token);
    }

    // The page's own load reports its failures; the start screen only waits for it, done or not.
    private async Task StartupReadyAsync(Task started)
    {
        await Task.WhenAny(started);
        if (ContentFrame.Content is IStartupContent page)
        {
            await Task.WhenAny(page.Ready);
        }
    }

    private void HideSplash()
    {
        SplashMark.IsLoading = false;
        Splash.IsHitTestVisible = false;
        var visual = ElementCompositionPreview.GetElementVisual(Splash);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0);
        fade.Duration = SplashFade;
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        batch.End();
        batch.Completed += (_, _) => Splash.Visibility = Visibility.Collapsed;
    }

    // Between a title's page (the rail) and any other (the open pane) the sidebar changes its width, and the content with
    // it: the old page squeezed or stretched while the pane moved, and the new one jumped in after it. Instead the old page
    // goes at once, the pane moves over the empty page, and the new page fades in once the pane stands (OnPaneSettled).
    private NavigationTransitionInfo? TransitionFor(AppRoute target)
    {
        bool fromPane = _navigation.Current is { } current && ShowsPane(current);
        if (!Shell.IsSignedIn || !fromPane || !ShowsPane(target) || IsDetail(target) == _isRail)
        {
            return null;
        }
        _revealPending = true;
        ContentFrame.OpacityTransition = null;
        ContentFrame.Opacity = 0;
        _reveal.Start();
        return new SuppressNavigationTransitionInfo();
    }

    private static bool ShowsPane(AppRoute route) => route is not (AppRoute.Player or AppRoute.Settings or AppRoute.Admin);

    private void OnPaneSettled(NavigationView sender, object args) => RevealPage();

    private void RevealPage()
    {
        _reveal.Stop();
        if (!_revealPending)
        {
            return;
        }
        _revealPending = false;
        ContentFrame.OpacityTransition = new ScalarTransition { Duration = RevealFade };
        ContentFrame.Opacity = 1;
    }

    // Opening or closing the pane, by the window or by the rail's pointer or a click beside it, turns the headers between
    // fold buttons and dividers, and the folded sections' entries between hidden and shown.
    private void OnPaneOpening(NavigationView sender, object args) => ShowPaneParts(open: true);

    private void OnPaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args) => ShowPaneParts(open: false);

    private void ShowPaneParts(bool open)
    {
        ShowHeaders(open);
        ShowSections(open);
        PlaceDock(animate: true, open);
        // Over the rail's pane while it is open over the page, the disc would sit on the sidebar.
        PageBackButton.Visibility = _showsPageBack && !(_isRail && open) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPageBackClick(object sender, RoutedEventArgs e) => _navigation.GoBack();

    // A folded section hides its entries, all but the page shown, so the sidebar still says where the viewer is. The
    // icon-only pane shows them all, as the web's rail does: it has no headers to open them by. Requests and
    // Notifications show only while the server offers them, and an entry the profile hid not at all but while it is the
    // page shown; a section with nothing left to show loses its header too.
    private void ShowSections() => ShowSections(Navigation.IsPaneOpen);

    private void ShowSections(bool open)
    {
        bool shown = true;
        NavigationViewItemHeader? header = null;
        var available = new Dictionary<NavigationViewItemHeader, bool>();
        foreach (object item in Navigation.MenuItems)
        {
            if (item is NavigationViewItemHeader sectionHeader && SectionOf(sectionHeader) is { } section)
            {
                header = sectionHeader;
                available[header] = false;
                shown = Sections.IsExpanded(section) || !open;
            }
            else if (item is NavigationViewItem entry)
            {
                bool current = ReferenceEquals(entry, Navigation.SelectedItem);
                bool kept = IsOffered(entry) && (current || !(entry.Tag is string tag && Layout.IsHidden(tag)));
                entry.Visibility = kept && (shown || current) ? Visibility.Visible : Visibility.Collapsed;
                if (header is not null && kept)
                {
                    available[header] = true;
                }
            }
        }
        foreach (var (sectionHeader, hasEntries) in available)
        {
            sectionHeader.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // Each section's entries in the profile's order (SidebarLayout), moved only when that differs from the order shown.
    private void ArrangeSections()
    {
        var items = Navigation.MenuItems;
        foreach (var sectionHeader in new[] { LibrariesHeader, DiscoverHeader, YourStuffHeader })
        {
            int start = items.IndexOf(sectionHeader) + 1;
            var entries = items.Skip(start).TakeWhile(item => item is NavigationViewItem).Cast<NavigationViewItem>().ToList();
            if (entries.DistinctBy(entry => entry.Tag as string).Count() != entries.Count)
            {
                // A library the server lists twice: the section stays as it comes.
                continue;
            }
            var order = Layout.Arrange(SectionOf(sectionHeader)!.Value, DefaultOrder(entries));
            var arranged = order.Select(tag => entries.First(entry => entry.Tag as string == tag)).ToList();
            if (arranged.SequenceEqual(entries))
            {
                continue;
            }
            var selected = Navigation.SelectedItem;
            foreach (var entry in entries)
            {
                items.Remove(entry);
            }
            for (int index = 0; index < arranged.Count; index++)
            {
                items.Insert(start + index, arranged[index]);
            }
            if (selected is not null && !ReferenceEquals(Navigation.SelectedItem, selected))
            {
                Navigation.SelectedItem = selected;
            }
        }
    }

    // The order a section's entries come in: the libraries as the server lists them, the others as the sidebar declares them.
    private List<string> DefaultOrder(IReadOnlyList<NavigationViewItem> entries)
    {
        var tags = entries.Select(entry => entry.Tag as string ?? "").ToList();
        var declared = Sidebar.Libraries.Select(SidebarLayout.TagOf).Concat(SidebarLayout.Entries.Select(entry => entry.Tag)).ToList();
        return [.. tags.OrderBy(tag => declared.IndexOf(tag) is var index and >= 0 ? index : int.MaxValue)];
    }

    private SidebarSection? SectionOf(NavigationViewItemHeader header) =>
        ReferenceEquals(header, LibrariesHeader) ? SidebarSection.Libraries
        : ReferenceEquals(header, DiscoverHeader) ? SidebarSection.Discover
        : ReferenceEquals(header, YourStuffHeader) ? SidebarSection.YourStuff
        : null;

    private bool IsOffered(NavigationViewItem item) => item.Tag switch
    {
        nameof(AppRoute.Requests) => Requests.IsEnabled,
        nameof(AppRoute.WatchParty) => WatchParty.IsAvailable,
        nameof(AppRoute.Notifications) => Notifications.IsAvailable,
        _ => true,
    };

    private static bool IsLibraryItem(NavigationViewItem item) =>
        item.Tag is string tag && tag.StartsWith(LibraryTagPrefix, StringComparison.Ordinal);

    private static string LibraryGlyph(string type) => SidebarGlyphs.Library(type);

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
        ShowChrome(route);
        ShowDockSpace();
        SelectItem(route, _navigation.CurrentParameter);
        RefreshNotifications();
    }

    // The unread count is read again on the way through the app, at most every half minute, as the web's sidebar does;
    // whether the server takes requests once per profile.
    private async void RefreshNotifications()
    {
        try
        {
            await Task.WhenAll(Notifications.RefreshAsync(), Requests.RefreshAsync(), WatchParty.RefreshAsync());
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
        // The settings and the admin area bring their own sidebar, as on the web.
        bool hidesPane = isPlayer || route is AppRoute.Settings or AppRoute.Admin;
        // A detail page's rail opens over the page (compact mode), not beside it: the page keeps its width.
        _isRail = IsDetail(route);
        _railHover.Stop();
        // The way back is the web's: no arrow in the sidebar, a disc at the top left of a page opened from another (a title,
        // a row in full, a collection, the servers); the sidebar's own pages have none, Back (Alt+Left) still leads home.
        _showsPageBack = _navigation.CanGoBack && route is not (null or AppRoute.Player or AppRoute.Settings or AppRoute.Admin or AppRoute.Home
            or AppRoute.Search or AppRoute.Library or AppRoute.List or AppRoute.Collections or AppRoute.ForYou or AppRoute.Calendar
            or AppRoute.Notifications or AppRoute.Requests or AppRoute.WatchParty);
        // Clear of the page's heading, which starts 48 px in: just right of the pane, a little further beside the rail, and
        // near the window's edge where there is no sidebar (signing in).
        bool paneShown = Shell.IsSignedIn && !hidesPane;
        PageBackButton.Margin = new Thickness(
            !paneShown ? 16 : _isRail ? Navigation.CompactPaneLength + 16 : Navigation.OpenPaneLength + 6, 12, 0, 0);
        var mode = hidesPane ? NavigationViewPaneDisplayMode.LeftMinimal
            : _isRail ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Left;
        bool visible = Shell.IsSignedIn && !hidesPane;
        // A pane comes back visible before its mode changes: a mode changed while it was hidden (the player) left the
        // rail drawing the open pane's layout, the server's name and labels cut off at its edge. A pane that goes away
        // takes its new mode first. Either way, visibility before IsPaneOpen: a pane that becomes visible opens again.
        // Each is set only when it changes: NavigationView lays itself out again for every mode it is given, the same one
        // too, and that took longer than a page switch's cards.
        if (visible)
        {
            SetPaneVisible(true);
            SetPaneMode(mode);
        }
        else
        {
            SetPaneMode(mode);
            SetPaneVisible(false);
        }
        bool wasOpen = Navigation.IsPaneOpen;
        bool open = Shell.IsSignedIn && !IsDetail(route) && route != AppRoute.Player;
        if (wasOpen != open)
        {
            Navigation.IsPaneOpen = open;
        }
        if (_revealPending && wasOpen == Navigation.IsPaneOpen)
        {
            // The pane was already where it goes (the rail had opened under the mouse): nothing to wait for.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RevealPage);
        }
        Grid.SetRow(Navigation, isPlayer ? 0 : 1);
        Grid.SetRowSpan(Navigation, isPlayer ? 2 : 1);
        AppTitle.Visibility = isPlayer ? Visibility.Collapsed : Visibility.Visible;
        ShowPaneParts(Navigation.IsPaneOpen);
    }

    private void SetPaneMode(NavigationViewPaneDisplayMode mode)
    {
        if (Navigation.PaneDisplayMode != mode)
        {
            Navigation.PaneDisplayMode = mode;
        }
    }

    private void SetPaneVisible(bool visible)
    {
        if (Navigation.IsPaneVisible != visible)
        {
            Navigation.IsPaneVisible = visible;
        }
    }

    // A title's page and a person's, the web's detail pages: the sidebar narrows to its rail beside them.
    private static bool IsDetail(AppRoute? route) => route is AppRoute.Item or AppRoute.Person;

    // The headers fold their sections in the open pane; in the icon-only rail each is the web's short divider. Set here,
    // not by NavigationView's pane states, which lag behind after the player's minimal mode. LIBRARIES only with libraries.
    private void ShowHeaders() => ShowHeaders(Navigation.IsPaneOpen);

    // Whether a header shows at all (LIBRARIES only with libraries) is ShowSections'.
    private void ShowHeaders(bool open)
    {
        LibrariesToggle.IsCompact = DiscoverToggle.IsCompact = YourStuffToggle.IsCompact = !open;
    }

    // The process ends with the window, and the player's stop carries where the viewer left off: the window goes
    // out of sight at once but closes only once the stop is through, or after a few seconds.
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeAfterStops)
        {
            return;
        }
        // Only playback ends: the window closes as it is, full screen or not.
        _player?.ViewModel.Leave();
        // So does a Watch Party's socket, with a close the room sees at once rather than a connection that times out.
        _stops.Track(_room.LeaveAsync());
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
                    or AppRoute.Requests or AppRoute.WatchParty:
                _navigation.ShowSection(route);
                break;
            // Settings and Admin open over the page they were opened from, and go back to it.
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
            // The room opens from the Watch Party hub, which stays picked.
            AppRoute.WatchPartyRoom => nameof(AppRoute.WatchParty),
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
