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
    /// <param name="outcome"><c>active</c> for the open ones; null for all.</param>
    public Task<MediaRequestPage> ListMineAsync(Uri server, int limit = MaxPageSize, string? cursor = null, string? outcome = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"The limit must be between 1 and {MaxPageSize}.");
        }
        string query = new QueryBuilder().Add("outcome", outcome).Add("limit", limit).Add("cursor", cursor).ToString();
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

    /// <summary>TMDB serves at most this many pages of any list, whatever total it reports.</summary>
    public const int TmdbMaxPage = 500;

    /// <summary>A page of one Discover row (<c>trending_movies</c>, <c>popular_series</c>, …).</summary>
    public Task<DiscoverSection> GetDiscoverSectionAsync(Uri server, string section, int page, CancellationToken cancellationToken = default)
    {
        if (!IsKey(section))
        {
            throw new ArgumentException($"'{section}' is not a Discover row.", nameof(section));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        return http.GetAsync(server, $"api/v2/requests/discover/{section}" + new QueryBuilder().Add("page", page), SiloAuth.Profile,
            SiloJsonContext.Default.DiscoverSection, cancellationToken);
    }

    /// <summary>The studios, networks or genres to browse.</summary>
    public Task<DiscoverBrandCollection> ListBrandsAsync(Uri server, DiscoverBrowseKind kind, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/requests/discover/{kind switch
        {
            DiscoverBrowseKind.Studio => "studios",
            DiscoverBrowseKind.Network => "networks",
            _ => "genres",
        }}", SiloAuth.Profile, SiloJsonContext.Default.DiscoverBrandCollection, cancellationToken);

    /// <summary>
    /// A page of a studio's movies, a network's series, or a genre's movies or series (<paramref name="mediaType"/>, a
    /// genre's only).
    /// </summary>
    /// <param name="sort"><c>popularity</c>, <c>vote_average</c> or <c>release_date</c>.</param>
    public Task<DiscoverBrowsePage> BrowseAsync(Uri server, DiscoverBrowseKind kind, string slug, string sort, int page, string? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        if (sort is not ("popularity" or "vote_average" or "release_date"))
        {
            throw new ArgumentException($"'{sort}' is not a sort of browse pages.", nameof(sort));
        }
        if (mediaType is not (null or "movie" or "series"))
        {
            throw new ArgumentException($"'{mediaType}' is not a media type of requests.", nameof(mediaType));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        string kindName = kind switch
        {
            DiscoverBrowseKind.Studio => "studio",
            DiscoverBrowseKind.Network => "network",
            _ => "genre",
        };
        string query = new QueryBuilder().Add("sort", sort).Add("page", page)
            .Add("media_type", kind == DiscoverBrowseKind.Genre ? mediaType ?? "movie" : null).ToString();
        return http.GetAsync(server, $"api/v2/requests/discover/browse/{kindName}/{Segment(slug)}{query}", SiloAuth.Profile,
            SiloJsonContext.Default.DiscoverBrowsePage, cancellationToken);
    }

    // A Discover row's key: lower-case words and numbers joined by underscores.
    private static bool IsKey(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    /// <summary>A title from TMDB in full, with what the viewer can do about it. 404 for one TMDB does not know or the profile may not see.</summary>
    public Task<RequestMediaDetail> GetDetailAsync(Uri server, string mediaType, long tmdbId, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/requests/detail/{TitlePath(mediaType, tmdbId)}", SiloAuth.Profile,
            SiloJsonContext.Default.RequestMediaDetail, cancellationToken);

    /// <summary>Requests a title. Not idempotent: never retried, a retry after a lost answer would request it twice.</summary>
    public Task<MediaRequest> CreateAsync(Uri server, MediaRequestCreate request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = TitlePath(request.MediaType, request.TmdbId);
        return http.PostAsync(server, "api/v2/requests", SiloAuth.Profile, request, SiloJsonContext.Default.MediaRequestCreate,
            SiloJsonContext.Default.MediaRequest, cancellationToken);
    }

    /// <summary>One request, which the account made or an admin may see.</summary>
    public Task<MediaRequest> GetAsync(Uri server, string id, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/requests/{Segment(id)}", SiloAuth.Profile, SiloJsonContext.Default.MediaRequest, cancellationToken);

    /// <summary>Asks to hear (PUT) or stops hearing (DELETE) when someone else's request for a title lands.</summary>
    public Task SetFollowAsync(Uri server, string mediaType, long tmdbId, bool follow, CancellationToken cancellationToken = default) =>
        http.SendAsync(follow ? HttpMethod.Put : HttpMethod.Delete, server, $"api/v2/requests/follows/{TitlePath(mediaType, tmdbId)}",
            SiloAuth.Profile, cancellationToken);

    /// <summary>Puts a title from TMDB on the watchlist; the answer says whether that also requested it.</summary>
    public Task<WatchlistTitleEntry> AddToWatchlistAsync(Uri server, string mediaType, long tmdbId, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Put, server, $"api/v2/watchlist/titles/{TitlePath(mediaType, tmdbId)}", SiloAuth.Profile,
            SiloJsonContext.Default.WatchlistTitleEntry, cancellationToken);

    public Task RemoveFromWatchlistAsync(Uri server, string mediaType, long tmdbId, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Delete, server, $"api/v2/watchlist/titles/{TitlePath(mediaType, tmdbId)}", SiloAuth.Profile, cancellationToken);

    // "movie/123": the only media types the routes take, and a positive TMDB id.
    private static string TitlePath(string mediaType, long tmdbId)
    {
        if (mediaType is not ("movie" or "series"))
        {
            throw new ArgumentException($"'{mediaType}' is not a media type of requests.", nameof(mediaType));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tmdbId);
        return $"{mediaType}/{tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string Segment(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id is "." or "..")
        {
            throw new ArgumentException($"'{id}' is not an id.", nameof(id));
        }
        return SiloHttp.Escape(id);
    }
}
