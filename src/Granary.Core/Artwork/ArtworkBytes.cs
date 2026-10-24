namespace Granary.Core.Artwork;

/// <summary>
/// An image as its server encoded it (JPEG, PNG, WebP …). Caches and every view that shows the image share
/// one instance, so the bytes can only be read.
/// </summary>
public sealed class ArtworkBytes
{
    private readonly byte[] _data;

    /// <summary>Takes ownership of <paramref name="data"/>: the caller must not change it afterwards.</summary>
    internal ArtworkBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
    }

    public int Length => _data.Length;

    public Stream OpenRead() => new MemoryStream(_data, writable: false);
}
