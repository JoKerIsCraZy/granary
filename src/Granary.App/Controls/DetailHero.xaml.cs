using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Collections;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>The hero of <see cref="Views.ItemDetailPage"/>; it shows and acts on <see cref="ViewModel"/>.</summary>
public sealed partial class DetailHero : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ItemDetailViewModel), typeof(DetailHero), new PropertyMetadata(null));

    // The Info dialog as Silage's: wide, most of the window's height.
    private const double InfoWidth = 980;
    private const double InfoHeightShare = 0.86;
    private const double InfoChrome = 170;

    public DetailHero()
    {
        InitializeComponent();
    }

    public ItemDetailViewModel? ViewModel
    {
        get => (ItemDetailViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // Static helpers because x:Bind function bindings call them; TitlePage shares them.
    public static ImageSource? LoadImage(Uri? url, int decodeWidth) => Artwork.Load(url, decodeWidth);

    public static Visibility Shows(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Style ChipStyle(bool isAccent) => (Style)Application.Current.Resources[isAccent ? "SiloAccentChipStyle" : "SiloChipStyle"];

    public static Brush ChipForeground(bool isAccent) => Resource(isAccent ? "AccentTextFillColorPrimaryBrush" : "SiloForegroundBrush");

    public static Brush ActiveForeground(bool isActive) => Resource(isActive ? "SiloPrimaryBrush" : "SiloForegroundBrush");

    // Segoe Fluent Icons: Heart, HeartFill; Add, Accept.
    public static string HeartGlyph(bool isFavorite) => isFavorite ? "" : "";

    public static string WatchlistGlyph(bool inWatchlist) => inWatchlist ? "" : "";

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    // A missing or expired logo: the display title takes its place.
    private void OnLogoFailed(object sender, ExceptionRoutedEventArgs e) => ViewModel?.OnLogoFailed();

    private void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DetailCrumb crumb } && ViewModel?.OpenCrumbCommand is { } command && command.CanExecute(crumb))
        {
            command.Execute(crumb);
        }
    }

    private void OnActionErrorClosed(InfoBar sender, object args) => ViewModel?.DismissActionError();

    // Everything the hero leaves out: the full overview, ratings, cast and crew by job, and each file's facts.
    private async void OnInfoClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } item || XamlRoot is null)
        {
            return;
        }
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = item.DisplayTitle,
                Content = new DetailInfoPanel(item.InfoSections) { MaxHeight = Math.Max(240, XamlRoot.Size.Height * InfoHeightShare - InfoChrome) },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            dialog.Resources["ContentDialogMaxWidth"] = InfoWidth;
            dialog.Resources["ContentDialogMaxHeight"] = XamlRoot.Size.Height * InfoHeightShare;
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // async void: nothing may escape (another dialog may already be open).
            App.Services.GetRequiredService<ILogger<DetailHero>>().LogWarning("The Info dialog could not be shown: {Error}", ex.GetType().Name);
        }
    }

    // The web's "Add to a collection": the profile's own manual collections, ticked where the title is in them.
    private async void OnAddToCollectionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { ContentId.Length: > 0 } item)
        {
            return;
        }
        try
        {
            var collections = App.Services.GetRequiredService<AddToCollectionViewModel>();
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Add to a collection",
                Content = new AddToCollectionPanel(collections),
                CloseButtonText = "Done",
                DefaultButton = ContentDialogButton.Close,
            };
            var shown = dialog.ShowAsync();
            try
            {
                await collections.LoadAsync(item.ContentId, item.DisplayTitle);
            }
            catch
            {
                // Not left open, empty and loading.
                dialog.Hide();
                throw;
            }
            await shown;
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A failed read or tick is the dialog's to say; this is anything else.
            App.Services.GetRequiredService<ILogger<DetailHero>>().LogError(ex, "Add to collection could not be shown");
        }
    }
}
