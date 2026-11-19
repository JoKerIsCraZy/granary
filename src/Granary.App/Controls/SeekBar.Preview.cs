using Granary.Core.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Granary.App.Controls;

// The bubble's picture, as Silo's web player shows it: the trickplay thumbnail of the time under the pointer; while its
// sheet loads, or without previews, the picture of the marked part or of the chapter there, or a placeholder. With a
// picture the bubble is 176 px wide and its lines sit left; with none it only shows the time. A download's kept previews
// (PlayerTrickplay.SheetFiles) are read from their files.
public sealed partial class SeekBar
{
    public static readonly DependencyProperty TrickplayProperty = DependencyProperty.Register(nameof(Trickplay),
        typeof(PlayerTrickplay), typeof(SeekBar), new PropertyMetadata(null, (sender, _) => ((SeekBar)sender).OnTrickplayChanged()));

    // The web's w-44, inside the bubble's 1 px edge.
    private const double PictureWidth = 174;

    // A few sheets stay decoded: scrubbing back and forth crosses the same ones.
    private const int KeptSheets = 3;

    // Most recently used last. Each sheet knows its own state, so the late events of one already let go change nothing.
    private readonly List<SheetEntry> _sheets = [];
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
        var sheet = Sheet(trickplay, tile.Sheet);
        if (tile.Sheet + 1 < trickplay.Sheets.Count)
        {
            // The next one too, so scrubbing forward rarely waits.
            Sheet(trickplay, tile.Sheet + 1);
        }
        if (!sheet.IsLoaded || sheet.HasFailed)
        {
            return false;
        }
        double scale = PictureWidth / trickplay.Width;
        double height = trickplay.Height * scale;
        TipTileImage.Source = sheet.Bitmap;
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

    // Sheets are told apart by their place: the previews they belong to are the bar's until they change, which clears them.
    private SheetEntry Sheet(PlayerTrickplay trickplay, int sheet)
    {
        int index = _sheets.FindIndex(s => s.Index == sheet);
        if (index >= 0)
        {
            var kept = _sheets[index];
            _sheets.RemoveAt(index);
            _sheets.Add(kept);
            return kept;
        }
        // Decoded as wide as the bubble shows it, not at the sheet's own size.
        int decodeWidth = (int)Math.Ceiling(trickplay.Columns * PictureWidth);
        SheetEntry entry;
        if (trickplay.SheetFiles is { } files)
        {
            // Kept with a download: read from its file on the thread pool, as a download's artwork is, never through an address.
            entry = new SheetEntry(sheet, new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical });
            LoadFile(entry, files[sheet]);
        }
        else
        {
            entry = new SheetEntry(sheet, Artwork.Load(trickplay.Sheets[sheet], decodeWidth)!);
            entry.Bitmap.ImageOpened += (_, _) => SheetLoaded(entry);
            entry.Bitmap.ImageFailed += (_, _) => entry.HasFailed = true;
        }
        _sheets.Add(entry);
        if (_sheets.Count > KeptSheets)
        {
            // A sheet let go is loaded afresh when it is wanted again, and a failed one is tried again.
            _sheets.RemoveAt(0);
        }
        return entry;
    }

    private void SheetLoaded(SheetEntry entry)
    {
        entry.IsLoaded = true;
        if (_sheets.Contains(entry))
        {
            ShowTipAgain();
        }
    }

    // async void: whatever escapes would end the app over one picture. A file that is gone or does not decode leaves the
    // bubble with the chapter's picture or the time, as a sheet that does not load does.
    private async void LoadFile(SheetEntry entry, string path)
    {
        try
        {
            using var stream = await LocalArtwork.ReadAsync(path);
            await entry.Bitmap.SetSourceAsync(stream);
            LocalArtwork.Forget(path);
        }
        catch (Exception ex)
        {
            LocalArtwork.SayFailed(path, ex);
            entry.HasFailed = true;
            return;
        }
        // Back on the UI thread, where each await above came back to.
        SheetLoaded(entry);
    }

    private sealed class SheetEntry(int index, BitmapImage bitmap)
    {
        public int Index { get; } = index;

        public BitmapImage Bitmap { get; } = bitmap;

        public bool IsLoaded { get; set; }

        public bool HasFailed { get; set; }
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
