namespace Granary.Core.Artwork;

/// <summary>A blurred still as a bitmap takes it: premultiplied blue, green, red and alpha.</summary>
public sealed record BlurredPicture(int Width, int Height, byte[] Bgra);

/// <summary>
/// The latest blurred stills (<see cref="SpoilerBlur"/>), by picture and the width they were blurred for, so a row scrolled
/// back and forth, or a card made again, does not fetch, decode and blur them again. A picture is known by its URL without
/// its signature (<see cref="ArtworkCacheKey"/>), which the server renews; widths within 16 px share a blur.
/// </summary>
public sealed class BlurredStills(int capacity = BlurredStills.DefaultCapacity)
{
    /// <summary>A few rows of wide cards; each picture is a few kilobytes.</summary>
    public const int DefaultCapacity = 48;

    private const double WidthStep = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Url, int Width), LinkedListNode<((string Url, int Width) Key, BlurredPicture Picture)>> _entries = [];
    private readonly LinkedList<((string Url, int Width) Key, BlurredPicture Picture)> _recent = new();

    /// <summary>The blur of <paramref name="url"/> made for about <paramref name="shownWidth"/>, or null.</summary>
    public BlurredPicture? Find(Uri url, double shownWidth)
    {
        var key = Key(url, shownWidth);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                return null;
            }
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Picture;
        }
    }

    public void Add(Uri url, double shownWidth, BlurredPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var key = Key(url, shownWidth);
        lock (_gate)
        {
            if (_entries.Remove(key, out var known))
            {
                _recent.Remove(known);
            }
            _entries[key] = _recent.AddFirst((key, picture));
            while (_recent.Count > Math.Max(1, capacity))
            {
                _entries.Remove(_recent.Last!.Value.Key);
                _recent.RemoveLast();
            }
        }
    }

    private static (string Url, int Width) Key(Uri url, double shownWidth) =>
        (ArtworkCacheKey.From(url), double.IsFinite(shownWidth) && shownWidth > 0 ? (int)Math.Round(shownWidth / WidthStep) : 0);
}
