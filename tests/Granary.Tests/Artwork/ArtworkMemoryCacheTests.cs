using Granary.Core.Artwork;

namespace Granary.Tests.Artwork;

public sealed class ArtworkMemoryCacheTests
{
    [Fact]
    public void Add_BeyondTheBudget_DropsTheLeastRecentlyUsedImage()
    {
        var cache = new ArtworkMemoryCache(capacityBytes: 100);
        cache.Add("a", Bytes(40));
        cache.Add("b", Bytes(40));
        Assert.True(cache.TryGet("a", out _));

        cache.Add("c", Bytes(40));

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(80, cache.Size);
    }

    [Fact]
    public void Add_AnImageLargerThanTheBudget_KeepsTheOthers()
    {
        var cache = new ArtworkMemoryCache(capacityBytes: 100);
        cache.Add("a", Bytes(40));

        cache.Add("huge", Bytes(101));

        Assert.False(cache.Contains("huge"));
        Assert.True(cache.Contains("a"));
    }

    [Fact]
    public void Add_TheSameKeyAgain_ReplacesTheImage()
    {
        var cache = new ArtworkMemoryCache(capacityBytes: 100);
        cache.Add("a", Bytes(40));

        cache.Add("a", Bytes(10));

        Assert.Equal(10, cache.Size);
        Assert.True(cache.TryGet("a", out var bytes));
        Assert.Equal(10, bytes.Length);
    }

    [Fact]
    public void Remove_ForgetsTheImage()
    {
        var cache = new ArtworkMemoryCache(capacityBytes: 100);
        cache.Add("a", Bytes(40));

        cache.Remove("a");

        Assert.False(cache.Contains("a"));
        Assert.Equal(0, cache.Size);
    }

    private static ArtworkBytes Bytes(int length) => new(new byte[length]);
}
