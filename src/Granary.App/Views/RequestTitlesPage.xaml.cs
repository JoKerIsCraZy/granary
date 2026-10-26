using Granary.App.Controls;
using Granary.Core.Abstractions;
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

/// <summary>A Discover row in full, or a studio's, network's or genre's titles; the parameter is a <see cref="RequestTitlesRouteParameter"/>.</summary>
public sealed partial class RequestTitlesPage : Page
{
    // A logo is drawn 96 px wide: the tile less its 8 px margins.
    private const int LogoWidth = 96;

    // The web's tile: gray-800 with a gray-700 ring for a studio or network, the muted fill for a genre or while loading.
    private static readonly Brush Gray = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x29, 0x37));
    private static readonly Brush GrayRing = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x37, 0x41, 0x51));
    private static readonly Brush Muted = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x27, 0x27, 0x2A));
    private static readonly Brush NoRing = new SolidColorBrush(Colors.Transparent);

    public RequestTitlesPage()
    {
        ViewModel = App.Services.GetRequiredService<RequestTitlesViewModel>();
        InitializeComponent();
    }

    public RequestTitlesViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.NavigationMode != NavigationMode.Back)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        try
        {
            await ViewModel.ShowAsync(e.Parameter as RequestTitlesRouteParameter, isReturn: e.NavigationMode == NavigationMode.Back);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the page.
            App.Services.GetRequiredService<ILogger<RequestTitlesPage>>().LogError(ex, "Titles to request could not be shown");
        }
    }

    // Static helpers because x:Bind function bindings call them.
    public static Brush TileBackground(bool isGenre, string tileText) => isGenre || tileText.Length == 0 ? Muted : Gray;

    public static Brush TileRing(bool isGenre, string tileText) => isGenre || tileText.Length == 0 ? NoRing : GrayRing;

    public static double TileFontSize(bool isGenre) => isGenre ? 14 : 12;

    public static BitmapImage? LogoImage(Uri? logo) => Artwork.Load(logo, LogoWidth);

    public static Visibility Unless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility HasText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    // Nothing matched, or the row does not exist (a studio that does not exist says so in its heading alone).
    public static Visibility ShowsMessage(bool isEmpty, bool isNotFound, string message) =>
        (isEmpty || isNotFound) && message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}
