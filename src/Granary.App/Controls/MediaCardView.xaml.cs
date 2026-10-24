using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

public sealed partial class MediaCardView : UserControl
{
    public static readonly DependencyProperty CardProperty =
        DependencyProperty.Register(nameof(Card), typeof(MediaCard), typeof(MediaCardView), new PropertyMetadata(null, OnCardChanged));

    // Decode at twice the shown width, so artwork stays sharp at 200 % scaling.
    private const int PosterDecodeWidth = 370;
    private const int LandscapeDecodeWidth = 640;

    public MediaCardView()
    {
        InitializeComponent();
    }

    public MediaCard? Card
    {
        get => (MediaCard?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    // Instance methods because x:Bind function bindings call them on the control.
    public Visibility Shows(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility HasProgress(double? progress) => progress is > 0 ? Visibility.Visible : Visibility.Collapsed;

    public double ProgressValue(double? progress) => Math.Clamp(progress ?? 0, 0, 1);

    public Style SubtitleStyle(MediaCardShape shape) => (Style)Application.Current.Resources[
        shape == MediaCardShape.Poster ? "SiloOverlineTextStyle" : "SiloCaptionTextStyle"];

    private static void OnCardChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((MediaCardView)sender).ShowArtwork((MediaCard?)e.NewValue);

    private void ShowArtwork(MediaCard? card)
    {
        // Containers are recycled: never let the previous card's artwork show under the new title.
        Placeholder.Visibility = Visibility.Visible;
        ArtImage.Source = card is null
            ? null
            : Artwork.Load(card.ImageUrl, card.Shape == MediaCardShape.Poster ? PosterDecodeWidth : LandscapeDecodeWidth);
        UpdateArtHeight(ActualWidth);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateArtHeight(e.NewSize.Width);

    private void UpdateArtHeight(double width)
    {
        if (width > 0)
        {
            Art.Height = Card?.Shape == MediaCardShape.Landscape ? width * 9 / 16 : width * 3 / 2;
        }
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => Placeholder.Visibility = Visibility.Collapsed;

    // A missing or expired artwork URL leaves the title placeholder in place.
    private void OnImageFailed(object sender, ExceptionRoutedEventArgs e) => ArtImage.Source = null;
}
