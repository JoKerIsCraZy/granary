using Granary.Core.Artwork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

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

    /// <summary>
    /// A picture of <paramref name="url"/> from the cache, decoded as wide as the view shows it, once it is decoded: for a view
    /// that keeps what it shows until the next one is ready (a profile's avatar). An SVG (Silo's preset avatars) is drawn as
    /// one, which the bitmap decoder cannot read. Null when the cache cannot get the image, it does not decode, or the wait
    /// was cancelled.
    /// </summary>
    public static async Task<ImageSource?> PictureAsync(Uri url, int decodeWidth, CancellationToken cancellationToken)
    {
        InMemoryRandomAccessStream? stream;
        bool isSvg;
        try
        {
            (stream, isSvg) = await CopyKnowingSvgAsync(url, cancellationToken);
        }
        catch (Exception ex)
        {
            Loader.Invalidate(url);
            Logger.LogWarning("Artwork {Path} could not be copied for decoding: {Failure}", url.AbsolutePath, ex.Message);
            return null;
        }
        if (stream is null || cancellationToken.IsCancellationRequested)
        {
            stream?.Dispose();
            return null;
        }
        using (stream)
        {
            try
            {
                if (isSvg)
                {
                    return await SvgAsync(url, stream);
                }
                var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical };
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch (Exception ex)
            {
                Loader.Invalidate(url);
                Logger.LogWarning("Artwork {Path} could not be decoded: {Failure}", url.AbsolutePath, ex.Message);
                return null;
            }
        }
    }

    // Drawn by Direct2D, which knows only part of SVG: a shape styled only through a <style> sheet is drawn black (AGENTS.md),
    // and markup it cannot read fails here, so the letter shows.
    private static async Task<ImageSource?> SvgAsync(Uri url, InMemoryRandomAccessStream stream)
    {
        var svg = new SvgImageSource();
        var status = await svg.SetSourceAsync(stream);
        if (status == SvgImageSourceLoadStatus.Success)
        {
            return svg;
        }
        Loader.Invalidate(url);
        Logger.LogWarning("Artwork {Path} could not be drawn as SVG: {Status}", url.AbsolutePath, status);
        return null;
    }

    /// <summary>
    /// The bytes of <paramref name="url"/> from the cache, as a stream for Windows to read itself (Windows' media controls):
    /// the presigned address stays in the app. Null when the cache cannot get the image or the wait was cancelled.
    /// </summary>
    public static Task<InMemoryRandomAccessStream?> StreamAsync(Uri url, CancellationToken cancellationToken) => CopyAsync(url, cancellationToken);

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

    // Runs on the UI thread, which a bitmap needs: only the loader's own work and the copy leave it.
    private static async Task FillAsync(BitmapImage bitmap, Uri url, CancellationToken cancellationToken)
    {
        InMemoryRandomAccessStream? stream = null;
        try
        {
            stream = await CopyAsync(url, cancellationToken);
        }
        catch (Exception ex)
        {
            // The bytes would not copy: as an image that does not decode.
            Loader.Invalidate(url);
            Logger.LogWarning("Artwork {Path} could not be copied for decoding: {Failure}", url.AbsolutePath, ex.Message);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            stream?.Dispose();
            return;
        }
        if (stream is not null)
        {
            using (stream)
            {
                try
                {
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
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            bitmap.UriSource = url;
        }
    }

    // The image copied out of the cache into native memory, which the decoder reads; the lease ends with the copy, so the
    // image's chunks can go to the next one. The wait is not cancelled but checked afterwards: a recycled card would
    // otherwise throw an exception per image while the user scrolls, and the download goes on for the cache anyway.
    private static async Task<InMemoryRandomAccessStream?> CopyAsync(Uri url, CancellationToken cancellationToken)
    {
        using var lease = await Loader.GetAsync(url, CancellationToken.None);
        if (lease is null || cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        // Off the UI thread: the stream is agile, and a write per chunk would each come back to it.
        return await Task.Run(() => NativeStreamAsync(lease.Bytes));
    }

    // As CopyAsync, also saying whether the image is SVG markup, read while the lease holds its bytes. Apart, so the cards'
    // images take no extra step.
    private static async Task<(InMemoryRandomAccessStream? Stream, bool IsSvg)> CopyKnowingSvgAsync(Uri url, CancellationToken cancellationToken)
    {
        using var lease = await Loader.GetAsync(url, CancellationToken.None);
        if (lease is null || cancellationToken.IsCancellationRequested)
        {
            return (null, false);
        }
        bool isSvg = lease.Bytes.IsSvg;
        // Off the UI thread: the stream is agile, and a write per chunk would each come back to it.
        return (await Task.Run(() => NativeStreamAsync(lease.Bytes)), isSvg);
    }

    // One native buffer the size of the image, filled chunk by chunk, and one write: every WinRT object .NET holds adds to
    // the collector's memory pressure (CsWinRT), and enough of it starts full collections. The stream adapter of a .NET
    // stream (AsRandomAccessStream) would also make a managed buffer as large as each read of the decoder.
    private static async Task<InMemoryRandomAccessStream> NativeStreamAsync(ArtworkBytes artwork)
    {
        var buffer = new Windows.Storage.Streams.Buffer((uint)artwork.Length);
        if (!WindowsRuntimeMarshal.TryGetDataUnsafe(buffer, out nint data))
        {
            throw new InvalidOperationException("The native buffer has no memory to copy into.");
        }
        int at = 0;
        foreach (var chunk in artwork.Chunks)
        {
            if (!MemoryMarshal.TryGetArray(chunk, out var segment))
            {
                throw new InvalidOperationException("An artwork chunk is not an array.");
            }
            Marshal.Copy(segment.Array!, segment.Offset, data + at, segment.Count);
            at += segment.Count;
        }
        buffer.Length = (uint)at;
        var stream = new InMemoryRandomAccessStream();
        try
        {
            await stream.WriteAsync(buffer);
            stream.Seek(0);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
