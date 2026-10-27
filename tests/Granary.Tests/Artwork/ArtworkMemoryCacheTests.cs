using Granary.Core.Artwork;

namespace Granary.Tests.Artwork;

public sealed class ArtworkMemoryCacheTests
{
    private const int Chunk = ArtworkChunkPool.ChunkSize;
    private readonly ArtworkChunkPool _pool = new(100 * Chunk);

    [Fact]
    public void Add_BeyondTheBudget_DropsTheLeastRecentlyUsedImage()
    {
        var cache = new ArtworkMemoryCache(capacityBytes: 5 * Chunk);
        cache.Add("a", Bytes(2 * Chunk));
        cache.Add("b", Bytes(2 * Chunk));
        Assert.True(Touch(cache, "a"));

        cache.Add("c", Bytes(2 * Chunk));

        Assert.True(cache.Contains("a"));
        Assert.False(cache.Contains("b"));
        Assert.True(cache.Contains("c"));
        Assert.Equal(4 * Chunk, cache.Size);
    }

    [Fact]
    public void TheBudget_CountsWholeChunks()
    {
        var cache = new ArtworkMemoryCache(capacityBytes: 5 * Chunk);

        cache.Add("a", Bytes(1));
        cache.Add("b", Bytes(Chunk + 1));

        Assert.Equal(3 * Chunk, cache.Size);
    }

    [Fact]
    public void Add_AnImageLargerThanTheBudget_KeepsTheOthers_AndLetsGoOfIt()
    {
        var cache = new ArtworkMemoryCache(capacityBytes: 2 * Chunk);
        cache.Add("a", Bytes(Chunk));

        cache.Add("huge", Bytes(3 * Chunk));

        Assert.False(cache.Contains("huge"));
        Assert.True(cache.Contains("a"));
        Assert.Equal(3, _pool.FreeChunks);
    }

    [Fact]
    public void Add_TheSameKeyAgain_ReplacesTheImage()
    {
        var cache = new ArtworkMemoryCache(capacityBytes: 5 * Chunk);
        cache.Add("a", Bytes(2 * Chunk));

        cache.Add("a", Bytes(10));

        Assert.Equal(Chunk, cache.Size);
        Assert.True(cache.TryHold("a", out var bytes));
        Assert.Equal(10, bytes.Length);
    }

    [Fact]
    public void Remove_ForgetsTheImage_AndItsChunksGoBack()
    {
        var cache = new ArtworkMemoryCache(capacityBytes: 5 * Chunk);
        cache.Add("a", Bytes(2 * Chunk));

        cache.Remove("a");

        Assert.False(cache.Contains("a"));
        Assert.Equal(0, cache.Size);
        Assert.Equal(2, _pool.FreeChunks);
    }

    [Fact]
    public void AnImageStillLent_KeepsItsChunks_UntilItsLeaseEnds()
    {
        var cache = new ArtworkMemoryCache(capacityBytes: 5 * Chunk);
        cache.Add("a", Bytes(2 * Chunk));
        Assert.True(cache.TryHold("a", out var held));
        var lease = new ArtworkLease(held);

        cache.Remove("a");

        Assert.Equal(0, _pool.FreeChunks);
        Assert.Equal(2 * Chunk, lease.Bytes.Length);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(2, _pool.FreeChunks);
        Assert.Throws<ObjectDisposedException>(() => lease.Bytes);
    }

    private ArtworkBytes Bytes(int length) => ArtworkBytes.From(new byte[length], _pool);

    // Holds and lets go at once, as a reader that only looks.
    private static bool Touch(ArtworkMemoryCache cache, string key)
    {
        if (!cache.TryHold(key, out var bytes))
        {
            return false;
        }
        new ArtworkLease(bytes).Dispose();
        return true;
    }
}
