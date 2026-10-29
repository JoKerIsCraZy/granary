using Granary.App.Controls;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>The Requests hub: Discover and the account's requests.</summary>
public sealed partial class RequestsPage : Page
{
    private const int PosterWidth = 48;

    private static readonly Brush Transparent = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush White = new SolidColorBrush(Colors.White);
    private static readonly Brush Secondary = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x27, 0x27, 0x2A));
    private static readonly Brush Destructive = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xDC, 0x26, 0x26));

    public RequestsPage()
    {
        ViewModel = App.Services.GetRequiredService<RequestsViewModel>();
        InitializeComponent();
        Loaded += (_, _) => Hero.FollowScroll(Scroller);
    }

    public RequestsViewModel ViewModel { get; }

    /// <summary>The parameter of Browse Discover.</summary>
    public RequestsTab DiscoverTab => RequestsTab.Discover;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Hero.Resume();
        try
        {
            // Back keeps the hub as it was; the sidebar reads it again.
            await ViewModel.ActivateAsync(isReturn: e.NavigationMode == NavigationMode.Back);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the page.
            Logger().LogError(ex, "The requests could not be shown");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Deactivate();
        // The page stays cached: its hero lets go of its pictures until it shows again.
        Hero.Suspend();
    }

    // The hero belongs to Discover: Yours shows its title and tabs at the top, as before. Static because the x:Bind function
    // bindings call them on the type.
    public static BrowseHeroTitle? DiscoverHero(BrowseHeroTitle? hero, bool isDiscover) => isDiscover ? hero : null;

    public static Thickness RowsViewMargin(BrowseHeroTitle? hero, bool isDiscover) => BrowseHero.RowsViewMargin(DiscoverHero(hero, isDiscover));

    public static Thickness RowsPadding(BrowseHeroTitle? hero, bool isDiscover) => BrowseHero.RowsPadding(DiscoverHero(hero, isDiscover));

    // The web's badge variants: Available filled red, Failed filled in the destructive red, approved and on their way in
    // the secondary fill, Pending outlined, closed ones outlined and muted.
    public static Brush BadgeBackground(RequestBadgeLook look) => look switch
    {
        RequestBadgeLook.Primary => Resource("SiloPrimaryBrush"),
        RequestBadgeLook.Destructive => Destructive,
        RequestBadgeLook.Secondary => Secondary,
        _ => Transparent,
    };

    public static Brush BadgeBorder(RequestBadgeLook look) => look switch
    {
        RequestBadgeLook.Outline or RequestBadgeLook.Muted => Resource("SiloBorderBrush"),
        _ => Transparent,
    };

    public static Brush BadgeForeground(RequestBadgeLook look) => look switch
    {
        RequestBadgeLook.Primary or RequestBadgeLook.Destructive => White,
        RequestBadgeLook.Muted => Resource("SiloMutedBrush"),
        _ => Resource("SiloForegroundBrush"),
    };

    public static Brush NoteForeground(bool isError) => isError ? Destructive : Resource("SiloMutedBrush");

    public static BitmapImage? Poster(Uri? url) => Artwork.Load(url, PosterWidth);

    public static bool Not(bool value) => !value;

    public static Visibility Unless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private static ILogger Logger() => App.Services.GetRequiredService<ILogger<RequestsPage>>();

    private void OnDiscoverChecked(object sender, RoutedEventArgs e) => ViewModel.SelectTabCommand.Execute(RequestsTab.Discover);

    private void OnYoursChecked(object sender, RoutedEventArgs e) => ViewModel.SelectTabCommand.Execute(RequestsTab.Yours);

    private void OnOpenInLibraryClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RequestRowViewModel row })
        {
            ViewModel.OpenInLibraryCommand.Execute(row);
        }
    }

    // Withdrawing asks first, as the web's dialog does.
    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RequestRowViewModel row } || XamlRoot is null)
        {
            return;
        }
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                Title = "Cancel this request?",
                Content = $"Your request for \"{row.Title}\" will be withdrawn before it is sent to the download automation.",
                PrimaryButtonText = "Cancel request",
                CloseButtonText = "Keep request",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.CancelCommand.ExecuteAsync(row);
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A failed cancellation is already shown by the page.
            Logger().LogError(ex, "A request could not be cancelled");
        }
    }
}
