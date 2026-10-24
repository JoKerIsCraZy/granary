using System.Collections.Concurrent;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Api.Images;

/// <summary>
/// Reads a server's image capabilities once and turns sizes into the query parameters it understands
/// (docs/api-contract.md, 7.10). A failed read falls back to the server's default sizes and is retried
/// on the next request; only a successful read is kept.
/// </summary>
public sealed class ImageParameterCache(ImagesApi images, ILogger<ImageParameterCache> logger)
{
    private readonly ConcurrentDictionary<string, Task<ImageCapabilities?>> _capabilities = new(StringComparer.Ordinal);

    public async Task<ImageParameters> GetAsync(ServerEntry server, ImageSizes sizes, CancellationToken cancellationToken = default)
    {
        var read = _capabilities.GetOrAdd(server.Id, _ => ReadAsync(server));
        var capabilities = await read.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (capabilities is null)
        {
            _capabilities.TryRemove(new KeyValuePair<string, Task<ImageCapabilities?>>(server.Id, read));
        }
        return sizes.For(capabilities);
    }

    // Never faults, so a cached task is either a result or null (which is not kept).
    private async Task<ImageCapabilities?> ReadAsync(ServerEntry server)
    {
        try
        {
            return await images.GetCapabilitiesAsync(server.BaseUri).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the image capabilities of server {ServerEntryId}; using default sizes", server.Id);
            return null;
        }
    }
}
