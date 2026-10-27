namespace Granary.Core.Artwork;

/// <summary>
/// The chunks images are held in, kept for the next image once one leaves the memory cache and nobody reads it any more. A
/// grid scrolled past hundreds of images then allocates no new image memory once the cache is full, and the collector has
/// no image bytes to copy between its generations; plain arrays did, and a backdrop or a large poster as one array went on
/// the large object heap, whose allocations start full collections. Safe to use from any thread.
/// </summary>
public sealed class ArtworkChunkPool
{
    /// <summary>Far below the 85,000 bytes of the large object heap; a poster of about 83 KB leaves 13 KB of its last chunk unused.</summary>
    public const int ChunkSize = 16 * 1024;

    private readonly Lock _gate = new();
    private readonly Stack<byte[]> _free = new();
    private readonly int _capacity;

    /// <param name="capacityBytes">At most this much is kept for reuse; chunks beyond it are left to the collector.</param>
    public ArtworkChunkPool(long capacityBytes) => _capacity = (int)Math.Clamp(capacityBytes / ChunkSize, 0, int.MaxValue);

    /// <summary>A pool that keeps nothing: every chunk is new.</summary>
    public static ArtworkChunkPool None { get; } = new(0);

    /// <summary>The chunks waiting for reuse.</summary>
    public int FreeChunks
    {
        get
        {
            lock (_gate)
            {
                return _free.Count;
            }
        }
    }

    /// <summary>A chunk to fill; its bytes are left over from the image before, so only what is written counts.</summary>
    internal byte[] Rent()
    {
        lock (_gate)
        {
            if (_free.TryPop(out var chunk))
            {
                return chunk;
            }
        }
        // On the pinned object heap, which the collector never compacts: a chunk lives long, in the cache and then in the
        // pool, and as an ordinary array would be copied from generation to generation.
        return GC.AllocateUninitializedArray<byte>(ChunkSize, pinned: true);
    }

    internal void Return(byte[] chunk)
    {
        lock (_gate)
        {
            if (_free.Count < _capacity)
            {
                _free.Push(chunk);
            }
        }
    }
}
