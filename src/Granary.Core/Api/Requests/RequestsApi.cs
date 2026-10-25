using Granary.Core.Api.Catalog;

namespace Granary.Core.Api.Requests;

/// <summary>Media requests for the signed-in account (docs/api-contract.md, 7.15).</summary>
public sealed class RequestsApi(SiloHttp http)
{
    /// <summary>The largest page <c>GET /api/v2/requests/mine</c> answers.</summary>
    public const int MaxPageSize = 50;

    public Task<RequestFeatureStatus> GetStatusAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/requests/status", SiloAuth.Profile, SiloJsonContext.Default.RequestFeatureStatus, cancellationToken);

    /// <summary>A page of the account's requests, newest first.</summary>
    public Task<MediaRequestPage> ListMineAsync(Uri server, int limit = MaxPageSize, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"The limit must be between 1 and {MaxPageSize}.");
        }
        string query = new QueryBuilder().Add("limit", limit).Add("cursor", cursor).ToString();
        return http.GetAsync(server, "api/v2/requests/mine" + query, SiloAuth.Profile, SiloJsonContext.Default.MediaRequestPage, cancellationToken);
    }

    /// <summary>Withdraws the account's own request while nothing has been sent for it yet; answers the request as it is now.</summary>
    public Task<MediaRequest> CancelAsync(Uri server, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id is "." or "..")
        {
            throw new ArgumentException($"'{id}' is not an id.", nameof(id));
        }
        return http.PostAsync(server, $"api/v2/requests/{SiloHttp.Escape(id)}/cancel", SiloAuth.Profile, new RequestCancel(),
            SiloJsonContext.Default.RequestCancel, SiloJsonContext.Default.MediaRequest, cancellationToken);
    }

    /// <summary>The Discover rows with their first titles.</summary>
    public Task<DiscoverSectionCollection> ListDiscoverAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/requests/discover", SiloAuth.Profile, SiloJsonContext.Default.DiscoverSectionCollection, cancellationToken);
}
