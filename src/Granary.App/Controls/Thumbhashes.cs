using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Granary.Core.Artwork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Granary.App.Controls;

/// <summary>
/// The blurred pictures of artwork's thumbhashes, as the Android app keeps them: a few hundred, the latest used, so a card
/// scrolled back into view shows its blur without working it out again. A list's hashes are worked out ahead on the thread
/// pool (<see cref="Warm"/>), so a card scrolled in only makes its bitmap. <see cref="Get"/> is UI thread only.
/// </summary>
internal static class Thumbhashes
{
    // Android's ThumbhashPainterCache holds 256; each picture is at most 32 × 32 pixels.
    private const int Capacity = 256;

    // Pictures worked out ahead, about 3 KB each; past this many the oldest go.
    private const int WarmCapacity = 2048;

    private static readonly Dictionary<string, LinkedListNode<(string Hash, ImageSource? Picture)>> Pictures = new(StringComparer.Ordinal);
    private static readonly LinkedList<(string Hash, ImageSource? Picture)> Recent = new();
    private static readonly ConcurrentDictionary<string, Pixels?> Warmed = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> WarmedOrder = new();

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
        var picture = Create(Warmed.TryGetValue(hash, out var warmed) ? warmed : Decode(hash));
        Pictures[hash] = Recent.AddFirst((hash, picture));
        if (Recent.Count > Capacity)
        {
            Pictures.Remove(Recent.Last!.Value.Hash);
            Recent.RemoveLast();
        }
        return picture;
    }

    /// <summary>Works out the pictures of <paramref name="hashes"/> on the thread pool, ahead of the cards that show them.</summary>
    public static void Warm(IEnumerable<string?> hashes)
    {
        List<string> todo = [.. hashes.OfType<string>()
            .Where(hash => !string.IsNullOrWhiteSpace(hash) && !Pictures.ContainsKey(hash) && !Warmed.ContainsKey(hash))
            .Distinct(StringComparer.Ordinal)
            .Take(WarmCapacity)];
        if (todo.Count == 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            foreach (string hash in todo)
            {
                if (Warmed.TryAdd(hash, Decode(hash)))
                {
                    WarmedOrder.Enqueue(hash);
                }
            }
            while (Warmed.Count > WarmCapacity && WarmedOrder.TryDequeue(out string? oldest))
            {
                Warmed.TryRemove(oldest, out _);
            }
        });
    }

    private static Pixels? Decode(string hash) =>
        ThumbHash.FromBase64(hash) is { } picture ? new Pixels(picture.Width, picture.Height, ThumbHash.ToPremultipliedBgra(picture)) : null;

    // A blur is a nicety: a picture the bitmap does not take leaves the card without one, never the card down.
    private static WriteableBitmap? Create(Pixels? picture)
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
                pixels.Write(picture.Bgra);
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

    // A decoded picture as the bitmap takes it: premultiplied blue, green, red and alpha.
    private sealed record Pixels(int Width, int Height, byte[] Bgra);
}
