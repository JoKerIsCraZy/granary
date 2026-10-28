using System.Runtime.CompilerServices;
using Granary.Core.Api.Images;

namespace Granary.Core.Api.Catalog;

/// <summary>
/// Home, libraries, browse, search, detail and the personal lists (docs/api-contract.md, 7). Every call
/// acts for the selected profile. <c>images</c> comes from <see cref="ImageSizes.For"/>; leave it out for
/// the server's default sizes.
/// </summary>
public sealed class CatalogApi(SiloHttp http)
{
    /// <summary>Which rating sources the server shows; a server without ratings answers 404.</summary>
    public Task<RatingsCapability> GetRatingsCapabilityAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/capabilities/ratings", SiloAuth.Profile, SiloJsonContext.Default.RatingsCapability, cancellationToken);

    /// <summary>Every Home row with its cards. Rows can be large (100 cards); hide rows with <c>total_count == 0</c>.</summary>
    public Task<SectionCollection> ListHomeSectionsAsync(Uri server, ImageParameters? images = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/home/sections" + Query(images), SiloAuth.Profile, SiloJsonContext.Default.SectionCollection, cancellationToken);

    /// <summary>One Home row; an unknown or hidden id is 404.</summary>
    public Task<Section> GetHomeSectionItemsAsync(Uri server, string sectionId, ImageParameters? images = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/home/sections/{Segment(sectionId)}/items{Query(images)}", SiloAuth.Profile, SiloJsonContext.Default.Section, cancellationToken);

    /// <summary>
    /// Hides a card from Continue Watching or Next Up (<see cref="HomeDismissal.For"/> picks the surface).
    /// Dismissing an episode or series drops the whole show from both rows until the profile watches it
    /// again: remove every card of that series locally.
    /// </summary>
    public Task DismissHomeItemAsync(Uri server, HomeDismissal dismissal, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Put, server, DismissalPath(dismissal), SiloAuth.Profile, dismissal.ToRequest(),
            SiloJsonContext.Default.HomeDismissalRequest, cancellationToken);

    /// <summary>Shows a dismissed card again (undo).</summary>
    public Task UndismissHomeItemAsync(Uri server, HomeDismissal dismissal, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Delete, server, DismissalPath(dismissal), SiloAuth.Profile, cancellationToken);

    /// <summary>The libraries the profile can see; sort and hide them as <see cref="Library"/> says.</summary>
    public Task<LibraryCollection> ListLibrariesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/user/libraries", SiloAuth.Profile, SiloJsonContext.Default.LibraryCollection, cancellationToken);

    /// <summary>A library's landing rows with their cards.</summary>
    public Task<SectionCollection> ListLibrarySectionsAsync(Uri server, string libraryId, ImageParameters? images = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/library/{Segment(libraryId)}/sections{Query(images)}", SiloAuth.Profile, SiloJsonContext.Default.SectionCollection, cancellationToken);

    public Task<Section> GetLibrarySectionItemsAsync(Uri server, string libraryId, string sectionId, ImageParameters? images = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/library/{Segment(libraryId)}/sections/{Segment(sectionId)}/items{Query(images)}", SiloAuth.Profile,
            SiloJsonContext.Default.Section, cancellationToken);

    /// <summary>
    /// One page of a listing. A stale cursor is 400 <c>invalid_cursor</c>: restart from page one. A search
    /// past its deadline is 503 with Retry-After.
    /// </summary>
    /// <exception cref="ArgumentException">The query is one the server refuses (see <see cref="CatalogQuery.ToQueryString"/>).</exception>
    public Task<CatalogBrowseCollection> BrowseAsync(Uri server, CatalogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return http.GetAsync(server, "api/v2/catalog" + query.ToQueryString(), SiloAuth.Profile, SiloJsonContext.Default.CatalogBrowseCollection, cancellationToken);
    }

    /// <summary>
    /// The letter index of <paramref name="query"/> in title order: each letter's count and the list position of its
    /// first title, to <see cref="CatalogQuery.Seek"/> to. Only the scope (library, type), the filters and the order
    /// of the query count; it indexes the query source only, never a collection or a search.
    /// </summary>
    public Task<CatalogLetters> GetLettersAsync(Uri server, CatalogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return http.PostAsync(server, "api/v2/catalog/letters", SiloAuth.Profile, CatalogLettersBody.For(query),
            SiloJsonContext.Default.JsonElement, SiloJsonContext.Default.CatalogLetters, cancellationToken);
    }

    /// <summary>
    /// The facet values (genres, ratings, …) of the scope of <paramref name="scope"/>: its source, scope ids
    /// and type; its filters, sort and paging are not sent. File-derived facets only with
    /// <paramref name="includeTechnical"/>.
    /// </summary>
    public Task<CatalogFilters> GetFiltersAsync(Uri server, CatalogQuery scope, bool includeTechnical = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        string query = scope.ScopeQuery().AddFlag("skip_technical", !includeTechnical).ToString();
        return http.GetAsync(server, "api/v2/catalog/filters" + query, SiloAuth.Profile, SiloJsonContext.Default.CatalogFilters, cancellationToken);
    }

    /// <summary>A movie, series, season or episode with the viewer's state.</summary>
    /// <param name="libraryId">The library it is viewed in; picks its presentation when it is in several.</param>
    public Task<CatalogItemDetail> GetItemAsync(Uri server, string id, string? libraryId = null, ImageParameters? images = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/catalog/items/{Segment(id)}{Query(images, libraryId)}", SiloAuth.Profile,
            SiloJsonContext.Default.CatalogItemDetail, cancellationToken);

    public Task<SeasonCollection> ListSeasonsAsync(Uri server, string seriesId, string? libraryId = null, ImageParameters? images = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/catalog/series/{Segment(seriesId)}/seasons{Query(images, libraryId)}", SiloAuth.Profile,
            SiloJsonContext.Default.SeasonCollection, cancellationToken);

    /// <summary>The episodes of a season by number (0 = specials).</summary>
    public Task<EpisodeCollection> ListEpisodesAsync(Uri server, string seriesId, int seasonNumber, string? libraryId = null, ImageParameters? images = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/catalog/series/{Segment(seriesId)}/seasons/{SeasonNumber(seasonNumber)}/episodes{Query(images, libraryId)}",
            SiloAuth.Profile, SiloJsonContext.Default.EpisodeCollection, cancellationToken);

    /// <summary>The episodes of a season item; a series id is 404.</summary>
    public Task<EpisodeCollection> ListSeasonEpisodesAsync(Uri server, string seasonId, string? libraryId = null, ImageParameters? images = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/catalog/items/{Segment(seasonId)}/episodes{Query(images, libraryId)}", SiloAuth.Profile,
            SiloJsonContext.Default.EpisodeCollection, cancellationToken);

    /// <summary>"More like this": cards similar by content, without titles the profile marked Not interested.</summary>
    /// <param name="limit">1..50; the server's default is 20 (the web client asks for 12).</param>
    public Task<CatalogItemCollection> ListSimilarAsync(Uri server, string itemId, int? limit = null, ImageParameters? images = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The limit must be between 1 and 50.");
        }
        string query = new QueryBuilder().Add("limit", limit).Add(images).ToString();
        return http.GetAsync(server, $"api/v2/recommendations/similar/{Segment(itemId)}{query}", SiloAuth.Profile,
            SiloJsonContext.Default.CatalogItemCollection, cancellationToken);
    }

    public Task<CatalogSearchCapabilities> GetSearchCapabilitiesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/catalog/search/capabilities", SiloAuth.Profile, SiloJsonContext.Default.CatalogSearchCapabilities, cancellationToken);

    /// <summary>
    /// People whose name words start with the words of <paramref name="q"/>, exact matches first. Only when
    /// <see cref="CatalogSearchCapabilities.PeopleMediaScope"/> is true.
    /// </summary>
    /// <param name="limit">1..100.</param>
    /// <param name="mediaScope">One of <see cref="MediaScopes"/>, for example <c>video</c>; null searches every scope.</param>
    public Task<PersonCollection> SearchPeopleAsync(Uri server, string q, int? limit = null, string? mediaScope = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The limit must be between 1 and 100.");
        }
        string query = new QueryBuilder().Add("q", q).Add("limit", limit).Add("media_scope", mediaScope).ToString();
        return http.GetAsync(server, "api/v2/catalog/people" + query, SiloAuth.Profile, SiloJsonContext.Default.PersonCollection, cancellationToken);
    }

    /// <summary>Adds the item to (PUT) or removes it from (DELETE) the profile's favorites; idempotent.</summary>
    public Task SetFavoriteAsync(Uri server, string itemId, bool isFavorite, CancellationToken cancellationToken = default) =>
        http.SendAsync(isFavorite ? HttpMethod.Put : HttpMethod.Delete, server, $"api/v2/favorites/{Segment(itemId)}", SiloAuth.Profile, cancellationToken);

    /// <summary>Adds the item to (PUT) or removes it from (DELETE) the profile's watchlist; idempotent.</summary>
    public Task SetWatchlistAsync(Uri server, string itemId, bool inWatchlist, CancellationToken cancellationToken = default) =>
        http.SendAsync(inWatchlist ? HttpMethod.Put : HttpMethod.Delete, server, $"api/v2/watchlist/{Segment(itemId)}", SiloAuth.Profile, cancellationToken);

    /// <summary>
    /// Marks a movie or series Not interested (PUT) or clears the mark (DELETE); idempotent. Recommendation and
    /// discovery rows leave a marked title out; browsing, search and the lists are unchanged. Episodes and seasons are 422.
    /// </summary>
    public Task SetNotInterestedAsync(Uri server, string itemId, bool notInterested, CancellationToken cancellationToken = default) =>
        http.SendAsync(notInterested ? HttpMethod.Put : HttpMethod.Delete, server, $"api/v2/not-interested/{Segment(itemId)}", SiloAuth.Profile,
            cancellationToken);

    /// <summary>
    /// Clears where the profile left off in a movie or episode ("Reset Progress"), keeping its watched state and history;
    /// a title without one is a no-op. It leaves Continue Watching with it.
    /// </summary>
    public Task ClearResumePositionAsync(Uri server, string itemId, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Delete, server, $"api/v2/progress/{Segment(itemId)}/position", SiloAuth.Profile, cancellationToken);

    /// <summary>
    /// Marks a movie, episode, season or series watched (POST) or unwatched (DELETE); a season or series
    /// covers every episode, and unwatching also clears the resume position. Non-retryable.
    /// </summary>
    public Task SetWatchedAsync(Uri server, string itemId, bool watched, CancellationToken cancellationToken = default) =>
        http.SendAsync(watched ? HttpMethod.Post : HttpMethod.Delete, server, $"api/v2/watched/{Segment(itemId)}", SiloAuth.Profile, cancellationToken);

    /// <summary>
    /// Clears the watch data of <paramref name="targets"/> for the acting profile: their history, watched state and resume
    /// position, up to now (the History page's Remove). Answers 204.
    /// </summary>
    public Task RemoveHistoryAsync(Uri server, IReadOnlyList<HistoryRemovalTarget> targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return http.SendAsync(HttpMethod.Post, server, "api/v2/history/remove", SiloAuth.Profile, new HistoryRemoval(targets),
            SiloJsonContext.Default.HistoryRemoval, cancellationToken);
    }

    private static string DismissalPath(HomeDismissal dismissal)
    {
        ArgumentNullException.ThrowIfNull(dismissal);
        return $"api/v2/home/dismissals/{Segment(dismissal.Surface)}/{Segment(dismissal.ItemId)}";
    }

    private static string Query(ImageParameters? images, string? libraryId = null) =>
        new QueryBuilder().Add("library_id", libraryId).Add(images).ToString();

    /// <summary>
    /// An id as one escaped path segment; content ids such as <c>movie:heat-1995</c> need escaping. "." and
    /// ".." stay unescaped and would be removed as dot segments, addressing another route.
    /// </summary>
    private static string Segment(string value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value is "." or "..")
        {
            throw new ArgumentException($"'{value}' is not an id.", name);
        }
        return SiloHttp.Escape(value);
    }

    private static string SeasonNumber(int seasonNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seasonNumber);
        return SiloHttp.Invariant($"{seasonNumber}");
    }
}
