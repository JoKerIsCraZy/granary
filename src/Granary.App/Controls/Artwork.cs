using Granary.Core.Artwork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Granary.App.Controls;

/// <summary>Loads catalog artwork through the app's artwork cache (<see cref="ArtworkLoader"/>).</summary>
internal static class Artwork
{
    private static ArtworkLoader Loader => field ??= App.Services.GetRequiredService<ArtworkLoader>();

    private static ILogger Logger => field ??= App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Artwork));

    /// <summary>
    /// A bitmap of <paramref name="url"/>, decoded only as wide as the view shows it, filled when the cache has the
    /// image; it raises ImageOpened then. When the cache cannot get the image, the bitmap loads the URL itself,
    /// so a broken image still raises ImageFailed. Cancelling (the view moved on to another item) leaves it empty.
    /// </summary>
    public static BitmapImage? Load(Uri? url, int decodeWidth, CancellationToken cancellationToken = default) =>
        url is { IsAbsoluteUri: true }
            ? Filled(new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical }, url, cancellationToken)
            : null;

    /// <summary>
    /// As <see cref="Load(Uri?, int, CancellationToken)"/>, decoded as high as the view shows it: a picture of another
    /// shape than its box (a backdrop on a poster card) then still fills it sharply.
    /// </summary>
    public static BitmapImage? LoadAtHeight(Uri? url, int decodeHeight, CancellationToken cancellationToken = default) =>
        url is { IsAbsoluteUri: true }
            ? Filled(new BitmapImage { DecodePixelHeight = decodeHeight, DecodePixelType = DecodePixelType.Logical }, url, cancellationToken)
            : null;

    private static BitmapImage Filled(BitmapImage bitmap, Uri url, CancellationToken cancellationToken)
    {
        Fill(bitmap, url, cancellationToken);
        return bitmap;
    }

    // async void: whatever escapes would end the app over one image.
    private static async void Fill(BitmapImage bitmap, Uri url, CancellationToken cancellationToken)
    {
        try
        {
            await FillAsync(bitmap, url, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Artwork {Path} could not be shown", url.AbsolutePath);
        }
    }

    // Runs on the UI thread, which a bitmap needs: only the loader's own work leaves it.
    private static async Task FillAsync(BitmapImage bitmap, Uri url, CancellationToken cancellationToken)
    {
        // The wait is not cancelled but checked afterwards: a recycled card would otherwise throw an exception
        // per image while the user scrolls, and the download goes on for the cache anyway.
        var artwork = await Loader.GetAsync(url, CancellationToken.None);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (artwork is not null)
        {
            try
            {
                using var stream = artwork.OpenRead().AsRandomAccessStream();
                await bitmap.SetSourceAsync(stream);
                return;
            }
            catch (Exception ex)
            {
                // The bytes do not decode: drop them from the cache, and let the bitmap try the URL.
                Loader.Invalidate(url);
                Logger.LogWarning("Artwork {Path} could not be decoded: {Failure}", url.AbsolutePath, ex.Message);
            }
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            bitmap.UriSource = url;
        }
    }
}
