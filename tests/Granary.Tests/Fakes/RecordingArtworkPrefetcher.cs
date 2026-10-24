using System.Collections.Concurrent;
using Granary.Core.Artwork;

namespace Granary.Tests.Fakes;

/// <summary>Records what is prefetched, in order, without fetching anything.</summary>
internal sealed class RecordingArtworkPrefetcher : IArtworkPrefetcher
{
    private readonly ConcurrentQueue<Uri> _urls = new();

    public IReadOnlyList<Uri> Urls => [.. _urls];

    public void Prefetch(IEnumerable<Uri> urls, CancellationToken cancellationToken = default)
    {
        foreach (var url in urls)
        {
            _urls.Enqueue(url);
        }
    }
}
