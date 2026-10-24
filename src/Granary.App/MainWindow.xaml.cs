using Granary.App.Services;
using Granary.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App;

public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;

    public MainWindow(NavigationService navigation)
    {
        _navigation = navigation;
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        _navigation.Attach(ContentFrame);
        _navigation.Navigated += (_, route) => SelectItem(route);
        _navigation.NavigateTo(AppRoute.Home);
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Enum.TryParse<AppRoute>(tag, out var route))
        {
            _navigation.NavigateTo(route);
        }
    }

    private void SelectItem(AppRoute route)
    {
        string tag = route.ToString();
        var item = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null && !ReferenceEquals(Navigation.SelectedItem, item))
        {
            Navigation.SelectedItem = item;
        }
    }
}
