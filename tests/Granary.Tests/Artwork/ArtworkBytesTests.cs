using Granary.Core.Artwork;

namespace Granary.Tests.Artwork;

/// <summary>Images held in pooled chunks far below the large object heap's 85,000 bytes.</summary>
public sealed class ArtworkBytesTests
{
    private const int Chunk = ArtworkBytes.ChunkSize;
    private readonly ArtworkChunkPool _pool = new(100 * Chunk);

    private static byte[] Image(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 31 + 7))];

    private static byte[] Read(ArtworkBytes bytes)
    {
        using var stream = bytes.OpenRead();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(300_000)]
    public async Task AStream_IsReadIntoWholeChunks(int length)
    {
        var image = Image(length);

        var bytes = await ArtworkBytes.ReadAsync(new TrickleStream(image), long.MaxValue, _pool, TestContext.Current.CancellationToken);

        Assert.NotNull(bytes);
        Assert.Equal(image, Read(bytes));
        Assert.Equal(length, bytes.Length);
        Assert.Equal((length + Chunk - 1) / Chunk * (long)Chunk, bytes.Footprint);
        Assert.Equal(image, bytes.Chunks.SelectMany(chunk => chunk.ToArray()));
    }

    [Fact]
    public async Task NothingAtAll_IsNone_AndItsChunkGoesBack()
    {
        Assert.Null(await ArtworkBytes.ReadAsync(new MemoryStream(), long.MaxValue, _pool, TestContext.Current.CancellationToken));
        Assert.Equal(1, _pool.FreeChunks);
    }

    [Fact]
    public async Task MoreThanAllowed_IsNone_AndItsChunksGoBack()
    {
        var bytes = await ArtworkBytes.ReadAsync(new MemoryStream(Image(150_000)), 100_000, _pool, TestContext.Current.CancellationToken);

        Assert.Null(bytes);
        Assert.Equal(7, _pool.FreeChunks);
    }

    [Fact]
    public async Task AFailedRead_GivesItsChunksBack()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            ArtworkBytes.ReadAsync(new BreakingStream(Image(40_000)), long.MaxValue, _pool, TestContext.Current.CancellationToken));

        Assert.Equal(3, _pool.FreeChunks);
    }

    [Fact]
    public void ChunksLetGo_AreUsedAgain_AndOnlyTheNewBytesShow()
    {
        var first = ArtworkBytes.From(Image(Chunk + 100), _pool);
        var reused = first.Chunks.Select(c => System.Runtime.InteropServices.MemoryMarshal.TryGetArray(c, out var s) ? s.Array : null).ToList();
        new ArtworkLease(first).Dispose();

        var second = ArtworkBytes.From([1, 2, 3], _pool);

        Assert.Equal([1, 2, 3], Read(second));
        Assert.Contains(second.Chunks.Select(c => System.Runtime.InteropServices.MemoryMarshal.TryGetArray(c, out var s) ? s.Array : null).Single(), reused);
        Assert.Throws<ObjectDisposedException>(() => first.OpenRead().ReadByte());
    }

    [Fact]
    public void TheStream_SeeksAndReadsAcrossChunks()
    {
        var image = Image(Chunk * 2 + 500);
        using var stream = ArtworkBytes.From(image).OpenRead();
        var part = new byte[1000];

        stream.Seek(Chunk - 400, SeekOrigin.Begin);
        int read = stream.Read(part);

        Assert.Equal(1000, read);
        Assert.Equal(image.AsSpan(Chunk - 400, 1000).ToArray(), part);
        Assert.Equal(image.Length, stream.Seek(0, SeekOrigin.End));
        Assert.Equal(0, stream.Read(part));
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public async Task Writing_GivesTheBytesBack()
    {
        var image = Image(140_000);
        using var copy = new MemoryStream();

        await ArtworkBytes.From(image).WriteToAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal(image, copy.ToArray());
    }

    [Fact]
    public void ThePool_KeepsNoMoreThanItsBudget()
    {
        var pool = new ArtworkChunkPool(2 * Chunk);
        var bytes = ArtworkBytes.From(new byte[3 * Chunk], pool);

        new ArtworkLease(bytes).Dispose();

        Assert.Equal(2, pool.FreeChunks);
    }

    // Hands out a few bytes at a time, as a network stream does.
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1500)], cancellationToken);
    }

    // Breaks after its bytes, as a connection that drops.
    private sealed class BreakingStream(byte[] data) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            return read > 0 ? read : throw new IOException("The connection dropped.");
        }
    }
}
