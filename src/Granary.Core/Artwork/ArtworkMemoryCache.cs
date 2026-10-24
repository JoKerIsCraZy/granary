using System.Diagnostics.CodeAnalysis;

namespace Granary.Core.Artwork;

/// <summary>
/// The most recently used images, up to a budget of bytes; the least recently used one goes first.
/// Safe to use from any thread.
/// </summary>
public sealed class ArtworkMemoryCache(long capacityBytes)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recency = [];
    private long _size;

    /// <summary>The bytes held right now.</summary>
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

    public bool TryGet(string key, [NotNullWhen(true)] out ArtworkBytes? bytes)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
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

    /// <summary>Keeps <paramref name="bytes"/> under <paramref name="key"/>; an image larger than the whole budget is not kept.</summary>
    public void Add(string key, ArtworkBytes bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate)
        {
            RemoveLocked(key);
            if (bytes.Length > capacityBytes)
            {
                return;
            }
            _entries[key] = _recency.AddFirst(new Entry(key, bytes));
            _size += bytes.Length;
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
            _size -= node.Value.Bytes.Length;
        }
    }

    private sealed record Entry(string Key, ArtworkBytes Bytes);
}
