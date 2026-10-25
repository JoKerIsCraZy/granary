using Granary.Core.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Granary.App.Controls;

// The bubble's picture, as Silo's web player shows it: the trickplay thumbnail of the time under the pointer; while its
// sheet loads, or without previews, the picture of the marked part or of the chapter there, or a placeholder. With a
// picture the bubble is 176 px wide and its lines sit left; with none it only shows the time.
public sealed partial class SeekBar
{
    public static readonly DependencyProperty TrickplayProperty = DependencyProperty.Register(nameof(Trickplay),
        typeof(PlayerTrickplay), typeof(SeekBar), new PropertyMetadata(null, (sender, _) => ((SeekBar)sender).OnTrickplayChanged()));

    // The web's w-44, inside the bubble's 1 px edge.
    private const double PictureWidth = 174;

    // A few sheets stay decoded: scrubbing back and forth crosses the same ones.
    private const int KeptSheets = 3;

    private readonly List<(Uri Url, BitmapImage Bitmap)> _sheets = [];
    private readonly HashSet<Uri> _loadedSheets = [];
    private readonly HashSet<Uri> _failedSheets = [];
    private Uri? _pictureUrl;
    private bool _pictureFailed;
    private (double Seconds, double X)? _tip;

    /// <summary>Seek-bar previews of the title; null without.</summary>
    public PlayerTrickplay? Trickplay
    {
        get => (PlayerTrickplay?)GetValue(TrickplayProperty);
        set => SetValue(TrickplayProperty, value);
    }

    // Fresh addresses are fresh sheets: what failed before may load now.
    private void OnTrickplayChanged()
    {
        _sheets.Clear();
        _loadedSheets.Clear();
        _failedSheets.Clear();
        ShowTipAgain();
    }

    // Shows the picture for seconds; false when the bubble has none and only shows the time.
    private bool ShowTipPicture(double seconds, PlaybackMarker? marker, PlaybackChapter? chapter)
    {
        if (Trickplay is { } trickplay && trickplay.TileAt(seconds) is { } tile && ShowTile(trickplay, tile))
        {
            return true;
        }
        TipTile.Visibility = Visibility.Collapsed;
        if (marker is null && chapter is null)
        {
            TipPicture.Visibility = Visibility.Collapsed;
            return false;
        }
        TipPicture.Visibility = Visibility.Visible;
        TipPicture.Height = PictureWidth * 9 / 16;
        TipPicture.Clip = null;
        Uri? url = marker is not null ? marker.ThumbnailUrl : chapter!.ThumbnailUrl;
        if (url != _pictureUrl)
        {
            _pictureUrl = url;
            _pictureFailed = false;
            TipImage.Source = Artwork.Load(url, (int)PictureWidth);
        }
        TipImage.Visibility = url is not null && !_pictureFailed ? Visibility.Visible : Visibility.Collapsed;
        return true;
    }

    // The thumbnail cut out of its sheet, scaled to the bubble; false while the sheet loads or when it failed.
    private bool ShowTile(PlayerTrickplay trickplay, TrickplayTile tile)
    {
        var sheet = Sheet(trickplay, tile.Url);
        if (tile.Sheet + 1 < trickplay.Sheets.Count)
        {
            // The next one too, so scrubbing forward rarely waits.
            Sheet(trickplay, trickplay.Sheets[tile.Sheet + 1]);
        }
        if (!_loadedSheets.Contains(tile.Url) || _failedSheets.Contains(tile.Url))
        {
            return false;
        }
        double scale = PictureWidth / trickplay.Width;
        double height = trickplay.Height * scale;
        TipTileImage.Source = sheet;
        TipTileImage.Width = trickplay.Columns * trickplay.Width * scale;
        TipTileImage.Height = trickplay.Rows * trickplay.Height * scale;
        Canvas.SetLeft(TipTileImage, -tile.X * scale);
        Canvas.SetTop(TipTileImage, -tile.Y * scale);
        TipPicture.Visibility = TipTile.Visibility = Visibility.Visible;
        TipImage.Visibility = Visibility.Collapsed;
        TipPicture.Height = height;
        TipPicture.Clip = new RectangleGeometry { Rect = new Rect(0, 0, PictureWidth, height) };
        return true;
    }

    private BitmapImage Sheet(PlayerTrickplay trickplay, Uri url)
    {
        int index = _sheets.FindIndex(s => s.Url == url);
        if (index >= 0)
        {
            var kept = _sheets[index];
            _sheets.RemoveAt(index);
            _sheets.Add(kept);
            return kept.Bitmap;
        }
        // Decoded as wide as the bubble shows it, not at the sheet's own size.
        var bitmap = Artwork.Load(url, (int)Math.Ceiling(trickplay.Columns * PictureWidth))!;
        bitmap.ImageOpened += (_, _) =>
        {
            _loadedSheets.Add(url);
            ShowTipAgain();
        };
        bitmap.ImageFailed += (_, _) => _failedSheets.Add(url);
        _sheets.Add((url, bitmap));
        if (_sheets.Count > KeptSheets)
        {
            _loadedSheets.Remove(_sheets[0].Url);
            _sheets.RemoveAt(0);
        }
        return bitmap;
    }

    // A picture that arrives while the bubble shows takes its place at once.
    private void ShowTipAgain()
    {
        if (_tip is { } tip && Tip.Opacity > 0)
        {
            ShowTip(tip.Seconds, tip.X);
        }
    }

    private void OnTipImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _pictureFailed = true;
        TipImage.Visibility = Visibility.Collapsed;
    }
}
