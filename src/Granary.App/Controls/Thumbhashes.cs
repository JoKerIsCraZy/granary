using System.Runtime.InteropServices.WindowsRuntime;
using Granary.Core.Artwork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Granary.App.Controls;

/// <summary>
/// The blurred pictures of artwork's thumbhashes, as the Android app keeps them: a few hundred, the latest used, so a card
/// scrolled back into view shows its blur without working it out again. UI thread only.
/// </summary>
internal static class Thumbhashes
{
    // Android's ThumbhashPainterCache holds 256; each picture is at most 32 × 32 pixels.
    private const int Capacity = 256;

    private static readonly Dictionary<string, LinkedListNode<(string Hash, ImageSource? Picture)>> Pictures = new(StringComparer.Ordinal);
    private static readonly LinkedList<(string Hash, ImageSource? Picture)> Recent = new();

    /// <summary>The blurred picture of <paramref name="hash"/>; null for none or one that does not decode.</summary>
    public static ImageSource? Get(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }
        if (Pictures.TryGetValue(hash, out var known))
        {
            Recent.Remove(known);
            Recent.AddFirst(known);
            return known.Value.Picture;
        }
        // A hash that does not decode is remembered as none, so it is not tried again.
        var picture = Create(ThumbHash.FromBase64(hash));
        Pictures[hash] = Recent.AddFirst((hash, picture));
        if (Recent.Count > Capacity)
        {
            Pictures.Remove(Recent.Last!.Value.Hash);
            Recent.RemoveLast();
        }
        return picture;
    }

    // A blur is a nicety: a picture the bitmap does not take leaves the card without one, never the card down.
    private static WriteableBitmap? Create(ThumbHash.Picture? picture)
    {
        if (picture is null)
        {
            return null;
        }
        try
        {
            var bitmap = new WriteableBitmap(picture.Width, picture.Height);
            using (var pixels = bitmap.PixelBuffer.AsStream())
            {
                pixels.Write(ThumbHash.ToPremultipliedBgra(picture));
            }
            bitmap.Invalidate();
            return bitmap;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "A thumbhash's {Width} × {Height} picture could not be made", picture.Width, picture.Height);
            return null;
        }
    }

    private static ILogger Logger => field ??= App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Thumbhashes));
}
