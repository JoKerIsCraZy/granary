using System.Diagnostics.CodeAnalysis;

namespace Granary.Core.Artwork;

/// <summary>
/// The most recently used images, up to a budget of memory (their chunks, whole); the least recently used one goes first.
/// The cache holds each image it keeps (<see cref="ArtworkBytes"/>) and lets go of it as it goes. Safe to use from any thread.
/// </summary>
public sealed class ArtworkMemoryCache(long capacityBytes)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recency = [];
    private long _size;

    /// <summary>The memory held right now.</summary>
    public long Size
    {
        get
        {
            lock (_gate)
            {
                return _size;
            }
        }
    }

    public bool Contains(string key)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(key);
        }
    }

    /// <summary>The image under <paramref name="key"/>, held for the caller, who lets go of it (an <see cref="ArtworkLease"/>).</summary>
    public bool TryHold(string key, [NotNullWhen(true)] out ArtworkBytes? bytes)
    {
        lock (_gate)
        {
            // The cache's own hold keeps it alive, so a hold for the caller always succeeds here.
            if (_entries.TryGetValue(key, out var node) && node.Value.Bytes.TryHold())
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                bytes = node.Value.Bytes;
                return true;
            }
        }
        bytes = null;
        return false;
    }

    /// <summary>
    /// Keeps <paramref name="bytes"/> under <paramref name="key"/>, taking over the caller's hold; an image larger than the
    /// whole budget is let go of at once.
    /// </summary>
    public void Add(string key, ArtworkBytes bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate)
        {
            RemoveLocked(key);
            if (bytes.Footprint > capacityBytes)
            {
                bytes.Release();
                return;
            }
            _entries[key] = _recency.AddFirst(new Entry(key, bytes));
            _size += bytes.Footprint;
            while (_size > capacityBytes && _recency.Last is { } oldest)
            {
                RemoveLocked(oldest.Value.Key);
            }
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            RemoveLocked(key);
        }
    }

    private void RemoveLocked(string key)
    {
        if (_entries.Remove(key, out var node))
        {
            _recency.Remove(node);
            _size -= node.Value.Bytes.Footprint;
            node.Value.Bytes.Release();
        }
    }

    private sealed record Entry(string Key, ArtworkBytes Bytes);
}
