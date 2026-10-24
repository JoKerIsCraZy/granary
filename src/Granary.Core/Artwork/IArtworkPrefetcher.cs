namespace Granary.Core.Artwork;

/// <summary>Fetches artwork into the cache before it is shown, so a page or a scrolled row shows it at once.</summary>
public interface IArtworkPrefetcher
{
    /// <summary>
    /// Queues <paramref name="urls"/> behind earlier batches. Images already cached are skipped, and so is
    /// whatever has not started when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    void Prefetch(IEnumerable<Uri> urls, CancellationToken cancellationToken = default);
}
