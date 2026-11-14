using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Granary.App.Controls;

/// <summary>
/// Artwork kept on this PC with a download (a poster, a still), decoded only as wide as the view shows it. The file is read on
/// the thread pool, so a downloads folder on a share that does not answer holds no page up, and from its path, which a URI
/// could misread (a <c>#</c> in a folder's name).
/// </summary>
internal static class LocalArtwork
{
    private static ILogger Logger => field ??= App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LocalArtwork));

    /// <summary>
    /// A bitmap of the file at <paramref name="path"/>, filled once it is read; null without a path. A file that cannot be read
    /// or decoded leaves it empty, which raises no ImageFailed: <paramref name="failed"/> hears of it instead, with the bitmap,
    /// on the UI thread.
    /// </summary>
    public static BitmapImage? Load(string? path, int decodeWidth, Action<BitmapImage>? failed = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical };
        Fill(bitmap, path, failed);
        return bitmap;
    }

    // async void: whatever escapes would end the app over one image. A file that is gone or does not decode shows nothing.
    private static async void Fill(BitmapImage bitmap, string path, Action<BitmapImage>? failed)
    {
        try
        {
            byte[] bytes = await Task.Run(() => File.ReadAllBytesAsync(path));
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            return;
        }
        catch (Exception ex)
        {
            // Only the kind: a file system error's message holds the full path, with the Windows user name.
            Logger.LogInformation("A download's artwork could not be shown ({Failure})", ex.GetType().Name);
        }
        // Back on the UI thread, where each await above came back to.
        failed?.Invoke(bitmap);
    }
}
