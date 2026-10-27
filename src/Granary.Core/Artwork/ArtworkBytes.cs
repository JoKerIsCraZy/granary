using System.Diagnostics;

namespace Granary.Core.Artwork;

/// <summary>
/// An image as its server encoded it (JPEG, PNG, WebP …), in chunks of an <see cref="ArtworkChunkPool"/>. Caches and every
/// view that shows the image share one instance, so the bytes can only be read.
/// </summary>
/// <remarks>
/// The bytes are counted out to their holders: the memory cache while it keeps the image, and each
/// <see cref="ArtworkLease"/> while its reader copies them. When the last one lets go, the chunks go back to the pool for the
/// next image, so reading is only for a holder.
/// </remarks>
public sealed class ArtworkBytes
{
    public const int ChunkSize = ArtworkChunkPool.ChunkSize;

    private readonly ArtworkChunkPool _pool;
    private readonly int _chunkCount;
    // Every chunk is ChunkSize long; the last is filled only up to Length. Null once the chunks went back to the pool.
    private byte[][]? _chunks;
    private int _holders = 1;

    private ArtworkBytes(byte[][] chunks, int length, ArtworkChunkPool pool)
    {
        _chunks = chunks;
        _chunkCount = chunks.Length;
        Length = length;
        _pool = pool;
    }

    public int Length { get; }

    /// <summary>The memory the image takes: its chunks, whole.</summary>
    public long Footprint => (long)_chunkCount * ChunkSize;

    /// <summary>The bytes in order, a chunk at a time, the last one only as far as it is filled.</summary>
    public IEnumerable<ReadOnlyMemory<byte>> Chunks
    {
        get
        {
            var chunks = Held();
            for (int i = 0; i < chunks.Length; i++)
            {
                yield return chunks[i].AsMemory(0, Math.Min(ChunkSize, Length - (i * ChunkSize)));
            }
        }
    }

    public Stream OpenRead() => new ChunkStream(this);

    /// <summary>A copy of <paramref name="data"/>, held by its caller.</summary>
    public static ArtworkBytes From(ReadOnlySpan<byte> data, ArtworkChunkPool? pool = null)
    {
        pool ??= ArtworkChunkPool.None;
        var chunks = new byte[(data.Length + ChunkSize - 1) / ChunkSize][];
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i] = pool.Rent();
            data.Slice(i * ChunkSize, Math.Min(ChunkSize, data.Length - (i * ChunkSize))).CopyTo(chunks[i]);
        }
        return new ArtworkBytes(chunks, data.Length, pool);
    }

    /// <summary>
    /// Reads <paramref name="source"/> to its end into chunks of <paramref name="pool"/>, held by its caller; null, with the
    /// chunks back in the pool, when it holds more than <paramref name="maxBytes"/> or nothing at all (no image is empty).
    /// </summary>
    public static async Task<ArtworkBytes?> ReadAsync(Stream source, long maxBytes, ArtworkChunkPool pool, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pool);
        var chunks = new List<byte[]>();
        bool kept = false;
        try
        {
            long total = 0;
            while (true)
            {
                var chunk = pool.Rent();
                chunks.Add(chunk);
                int filled = await FillAsync(source, chunk, cancellationToken).ConfigureAwait(false);
                total += filled;
                if (total > maxBytes)
                {
                    return null;
                }
                if (filled == 0)
                {
                    chunks.RemoveAt(chunks.Count - 1);
                    pool.Return(chunk);
                    break;
                }
                if (filled < ChunkSize)
                {
                    break;
                }
            }
            if (total == 0)
            {
                return null;
            }
            kept = true;
            return new ArtworkBytes([.. chunks], (int)total, pool);
        }
        finally
        {
            if (!kept)
            {
                chunks.ForEach(pool.Return);
            }
        }
    }

    public async Task WriteToAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        foreach (var chunk in Chunks)
        {
            await destination.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One more holder; false when the last one already let go.</summary>
    internal bool TryHold()
    {
        while (true)
        {
            int holders = Volatile.Read(ref _holders);
            if (holders == 0)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _holders, holders + 1, holders) == holders)
            {
                return true;
            }
        }
    }

    /// <summary>A holder lets go; the last one gives the chunks back to the pool.</summary>
    internal void Release()
    {
        int holders = Interlocked.Decrement(ref _holders);
        Debug.Assert(holders >= 0, "Artwork bytes were let go of more often than they were held.");
        if (holders == 0 && Interlocked.Exchange(ref _chunks, null) is { } chunks)
        {
            foreach (var chunk in chunks)
            {
                _pool.Return(chunk);
            }
        }
    }

    private byte[][] Held() => Volatile.Read(ref _chunks) ?? throw new ObjectDisposedException(nameof(ArtworkBytes), "The image's chunks went back to the pool.");

    private static async Task<int> FillAsync(Stream source, byte[] chunk, CancellationToken cancellationToken)
    {
        int filled = 0;
        while (filled < chunk.Length)
        {
            int read = await source.ReadAsync(chunk.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            filled += read;
        }
        return filled;
    }

    /// <summary>A read-only, seekable view of the chunks, for a holder.</summary>
    private sealed class ChunkStream(ArtworkBytes bytes) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var chunks = bytes.Held();
            int copied = 0;
            while (copied < buffer.Length && _position < bytes.Length)
            {
                int start = (int)(_position % ChunkSize);
                int count = (int)Math.Min(Math.Min(ChunkSize - start, buffer.Length - copied), bytes.Length - _position);
                chunks[(int)(_position / ChunkSize)].AsSpan(start, count).CopyTo(buffer[copied..]);
                copied += count;
                _position += count;
            }
            return copied;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<int>(cancellationToken);
            }
            try
            {
                return ValueTask.FromResult(Read(buffer.Span));
            }
            catch (ObjectDisposedException ex)
            {
                return ValueTask.FromException<int>(ex);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => bytes.Length + offset,
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
