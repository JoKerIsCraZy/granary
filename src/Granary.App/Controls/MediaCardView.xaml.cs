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

    // Cancelled when the container is recycled for another card, so the old card's artwork stays out.
    private CancellationTokenSource? _artwork;

    public MediaCardView()
    {
        InitializeComponent();
        Show(null);
    }

    public MediaCard? Card
    {
        get => (MediaCard?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    private static void OnCardChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((MediaCardView)sender).Show((MediaCard?)e.NewValue);

    // Every part is set for every card: containers are recycled, so nothing of the previous card may stay.
    private void Show(MediaCard? card)
    {
        Placeholder.Text = card?.Title ?? "";
        Placeholder.Visibility = Visibility.Visible;
        _artwork?.Cancel();
        _artwork = card?.ImageUrl is null ? null : new CancellationTokenSource();
        ArtImage.Source = card is null || _artwork is null
            ? null
            : Artwork.Load(card.ImageUrl, card.Shape == MediaCardShape.Poster ? PosterDecodeWidth : LandscapeDecodeWidth, _artwork.Token);

        SetBadge(ResolutionBadge, ResolutionText, card?.ResolutionBadge);
        SetBadge(AudioBadge, AudioText, card?.AudioBadge);
        SetBadge(StatusBadge, StatusText, card?.StatusBadge);
        SetBadge(RatingBadge, RatingText, card?.RatingBadge);
        WatchedMark.Visibility = card?.IsWatched == true ? Visibility.Visible : Visibility.Collapsed;

        bool hasProgress = card?.Progress is > 0;
        Progress.Value = hasProgress ? Math.Clamp(card!.Progress!.Value, 0, 1) : 0;
        Progress.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;

        TitleText.Text = card?.Title ?? "";
        SubtitleText.Style = (Style)Application.Current.Resources[
            card?.Shape == MediaCardShape.Landscape ? "SiloCaptionTextStyle" : "SiloOverlineTextStyle"];
        SetText(SubtitleText, card?.Subtitle);
        SetText(CaptionText, card?.Caption);

        UpdateArtHeight(ActualWidth);
    }

    private static void SetBadge(Border badge, TextBlock text, string? value)
    {
        text.Text = value ?? "";
        badge.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetText(TextBlock text, string? value)
    {
        text.Text = value ?? "";
        text.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
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
