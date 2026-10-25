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

/// <summary>The profile's notifications.</summary>
public sealed partial class NotificationsPage : Page
{
    private const int PosterWidth = 44;
    private const double ColumnWidth = 768;

    private static readonly Brush ReadBackground = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush UnreadBackground = new SolidColorBrush(Color(0x0F));

    public NotificationsPage()
    {
        ViewModel = App.Services.GetRequiredService<NotificationsViewModel>();
        InitializeComponent();
    }

    public NotificationsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            // Back keeps the inbox as it was; the sidebar reads it again.
            await ViewModel.ActivateAsync(isReturn: e.NavigationMode == NavigationMode.Back);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the page.
            App.Services.GetRequiredService<ILogger<NotificationsPage>>().LogError(ex, "The notifications could not be shown");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Deactivate();
    }

    // Static because x:Bind function bindings in the row template call them on the type.
    public static Brush RowBackground(bool isUnread) => isUnread ? UnreadBackground : ReadBackground;

    public static FontFamily TitleFont(bool isUnread) =>
        (FontFamily)Application.Current.Resources[isUnread ? "SiloFontSemiBold" : "SiloFontMedium"];

    public static BitmapImage? Poster(Uri? url) => Artwork.Load(url, PosterWidth);

    public static double SwitchOpacity(bool enabled) => enabled ? 1 : 0.5;

    private static Windows.UI.Color Color(byte alpha) => Windows.UI.Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);

    // A column capped by MaxWidth in a ScrollViewer ends up off centre, so its width is set here.
    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => Column.Width = Math.Min(ColumnWidth, e.NewSize.Width);

    // The switches are read each time the popover opens, as the web's.
    private async void OnPreferencesOpening(object? sender, object e)
    {
        try
        {
            await ViewModel.Preferences.LoadAsync();
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<NotificationsPage>>().LogError(ex, "The notification preferences could not be shown");
        }
    }
}
