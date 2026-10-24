namespace Granary.Core.Artwork;

public sealed record ArtworkLoaderOptions
{
    /// <summary>Encoded images kept in memory: about 3,000 posters.</summary>
    public long MemoryCapacityBytes { get; init; } = 96L * 1024 * 1024;

    /// <summary>Downloads for images on screen at the same time; over HTTP/2 they share one connection.</summary>
    public int MaxConcurrentDownloads { get; init; } = 12;

    /// <summary>Downloads for prefetched images at the same time, beside those for images on screen.</summary>
    public int PrefetchWorkers { get; init; } = 4;

    /// <summary>
    /// How long one download may take, body included. HttpClient's own timeout ends with the headers, so a
    /// server that stops sending mid-body would otherwise hold a download slot for good.
    /// </summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A larger answer is refused: no artwork variant comes near it.</summary>
    public long MaxImageBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>How long a failed image is not asked for again.</summary>
    public TimeSpan FailureHold { get; init; } = TimeSpan.FromMinutes(1);
}
