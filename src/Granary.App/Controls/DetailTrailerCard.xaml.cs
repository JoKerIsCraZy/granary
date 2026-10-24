using Granary.Core.ViewModels.Detail;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

public sealed partial class DetailTrailerCard : UserControl
{
    public static readonly DependencyProperty TrailerProperty =
        DependencyProperty.Register(nameof(Trailer), typeof(DetailTrailer), typeof(DetailTrailerCard), new PropertyMetadata(null, OnTrailerChanged));

    private const int ThumbnailDecodeWidth = 560;

    public DetailTrailerCard()
    {
        InitializeComponent();
    }

    public DetailTrailer? Trailer
    {
        get => (DetailTrailer?)GetValue(TrailerProperty);
        set => SetValue(TrailerProperty, value);
    }

    // YouTube's own thumbnail, as on the web; it needs no credentials.
    private static void OnTrailerChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((DetailTrailerCard)sender).Thumbnail.Source = Artwork.Load(((DetailTrailer?)e.NewValue)?.ThumbnailUrl, ThumbnailDecodeWidth);

    private void OnThumbnailFailed(object sender, ExceptionRoutedEventArgs e) => Thumbnail.Source = null;
}
