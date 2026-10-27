namespace Granary.Core.Artwork;

/// <summary>
/// An image lent out of the artwork cache: read <see cref="Bytes"/>, then dispose the lease, after which the image's chunks
/// can go to the next image. A lease never disposed only keeps its chunks from the pool.
/// </summary>
public sealed class ArtworkLease : IDisposable
{
    private ArtworkBytes? _bytes;

    /// <summary>Takes over one hold on <paramref name="bytes"/>.</summary>
    internal ArtworkLease(ArtworkBytes bytes) => _bytes = bytes;

    public ArtworkBytes Bytes => Volatile.Read(ref _bytes) ?? throw new ObjectDisposedException(nameof(ArtworkLease));

    public void Dispose() => Interlocked.Exchange(ref _bytes, null)?.Release();
}
