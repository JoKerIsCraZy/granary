using Granary.Core.Artwork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
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
