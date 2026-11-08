using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Api.Catalog;

/// <summary>
/// Whether one of the profile's libraries has box sets on (docs/api-contract.md, 7.18), which a movie's page asks before it
/// reads its box set, as the web's <c>useItemBoxSet</c> does: a server without box sets, or with them off everywhere, is not
/// asked on every movie. The list comes from <c>GET /api/v2/user/libraries</c> once per server and profile, again once it is
/// <see cref="StaleAfter"/> old (the web's staleTime); a read that fails answers no and is tried again the next time.
/// </summary>
public sealed class BoxSetLibraries(CatalogApi catalog, TimeProvider time, ILogger<BoxSetLibraries> logger)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    // The read for one server and profile, shared by the pages that ask meanwhile; its answer is null when it failed.
    private (string ServerId, string? ProfileId, DateTimeOffset At, Task<bool?> Read)? _known;

    public async Task<bool> AnyAsync(ServerEntry server, string? profileId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        Task<bool?> read;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_known is not { } known || known.ServerId != server.Id || known.ProfileId != profileId || now - known.At >= StaleAfter
                || known.Read is { IsCompleted: true, Result: null })
            {
                // Not one page's to cancel: the others wait for the same read.
                _known = (server.Id, profileId, now, ReadAsync(server.BaseUri));
            }
            read = _known.Value.Read;
        }
        return await read.WaitAsync(cancellationToken).ConfigureAwait(false) == true;
    }

    private async Task<bool?> ReadAsync(Uri server)
    {
        try
        {
            var libraries = await catalog.ListLibrariesAsync(server).ConfigureAwait(false);
            return libraries.Items.Any(library => library.BoxSetsEnabled);
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException or System.Text.Json.JsonException or OperationCanceledException)
        {
            logger.LogWarning("Reading which libraries have box sets failed: {Failure}", ex is SiloApiException api ? $"{api.Status} {api.Code}" : ex.GetType().Name);
            return null;
        }
    }
}
