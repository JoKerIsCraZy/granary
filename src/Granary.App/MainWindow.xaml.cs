using Granary.App.Services;
using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace Granary.App;

public sealed partial class MainWindow : Window
{
    private const string LibraryTagPrefix = "Library:";

    private readonly NavigationService _navigation;

    public MainWindow(NavigationService navigation, ShellViewModel shell, SidebarViewModel sidebar)
    {
        _navigation = navigation;
        Shell = shell;
        Sidebar = sidebar;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        StyleCaptionButtons();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));

        _navigation.Attach(ContentFrame);
        _navigation.Navigated += OnNavigated;
        Sidebar.Libraries.CollectionChanged += (_, _) => ShowLibraries();
        ShowLibraries();
    }

    /// <summary>Routes by session state; the app starts it once the window is up.</summary>
    public ShellViewModel Shell { get; }

    public SidebarViewModel Sidebar { get; }

    // Instance method because x:Bind function bindings call it on the window.
    public string ProfileMenuName(string profileName) => $"Profile menu: {profileName}";

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
        LibrariesHeader.Visibility = Sidebar.Libraries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_navigation.Current is { } route)
        {
            SelectItem(route, _navigation.CurrentParameter);
        }
    }

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
        // A disabled arrow on pages without history is only noise.
        Navigation.IsBackEnabled = _navigation.CanGoBack;
        Navigation.IsBackButtonVisible = _navigation.CanGoBack
            ? NavigationViewBackButtonVisible.Visible
            : NavigationViewBackButtonVisible.Collapsed;
        // Detail pages get the full width, as on the web: the sidebar shrinks to its icons. While the pane is
        // hidden (sign-in pages) it must stay closed: an open hidden pane still takes its width, empty.
        Navigation.IsPaneOpen = Shell.IsSignedIn && route is not (AppRoute.Item or AppRoute.Player);
        SelectItem(route, _navigation.CurrentParameter);
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
                _navigation.NavigateTo(AppRoute.Library, tag[LibraryTagPrefix.Length..]);
                break;
            case string tag when Enum.TryParse<AppRoute>(tag, out var route):
                _navigation.NavigateTo(route);
                break;
        }
    }

    private void SelectItem(AppRoute route, object? parameter)
    {
        string tag = route == AppRoute.Library ? LibraryTagPrefix + parameter : route.ToString();
        var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>()
            .FirstOrDefault(i => i.Tag as string == tag);
        // Pages without a sidebar entry (details, "Explore all") select nothing.
        if (!ReferenceEquals(Navigation.SelectedItem, item))
        {
            Navigation.SelectedItem = item;
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
            _navigation.NavigateTo(AppRoute.Search);
        }
    }
}
