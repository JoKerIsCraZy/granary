namespace Granary.Core.Api.Catalog;

/// <summary><c>{ "sections": [...] }</c> of <c>GET /api/v2/home/sections</c> and <c>GET /api/v2/library/{id}/sections</c>.</summary>
public sealed record SectionCollection(IReadOnlyList<Section> Sections);

/// <summary>
/// A Home or library row with its cards (docs/api-contract.md, 7.1). Never key logic on <see cref="Id"/>:
/// administrators rename and add rows. A row returns up to <see cref="ItemLimit"/> cards and takes no
/// paging; "See all" is <see cref="CatalogQuery.ForHomeSection"/> or <see cref="CatalogQuery.ForLibrarySection"/>.
/// </summary>
public sealed record Section
{
    public string Id { get; init => field = value ?? ""; } = "";

    /// <summary>Open set, see <see cref="SectionTypes"/>.</summary>
    public string SectionType { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";

    public bool Featured { get; init; }

    public int ItemLimit { get; init; }

    public bool IsCustom { get; init; }

    public bool Customized { get; init; }

    /// <summary>
    /// Cards the row has in total, at least <see cref="Items"/>' count. Hide rows with 0; a row that
    /// came back empty with a positive count is refetched with <c>GetHomeSectionItemsAsync</c>.
    /// </summary>
    public int TotalCount { get; init; }

    public IReadOnlyList<CatalogItem> Items { get; init => field = value ?? []; } = [];
}

/// <summary>Known values of <see cref="Section.SectionType"/>; the set is open.</summary>
public static class SectionTypes
{
    /// <summary>Also backs Continue Listening and Reading: filter its cards by type.</summary>
    public const string ContinueWatching = "continue_watching";
    public const string NextUp = "next_up";
    public const string RecentlyAdded = "recently_added";
    public const string RecentlyReleased = "recently_released";
    public const string Watchlist = "watchlist";
    public const string Favorites = "favorites";
    public const string Genre = "genre";
    public const string Collection = "collection";
    public const string RecommendedForYou = "recommended_for_you";
    public const string BecauseYouWatched = "because_you_watched";
}

/// <summary>The Home surfaces a card can be dismissed from.</summary>
public static class HomeSurfaces
{
    public const string ContinueWatching = "continue_watching";
    public const string NextUp = "next_up";
}

/// <summary>
/// How a Home card is removed, chosen per card and not per row (docs/api-contract.md, 7.1): a card with
/// <c>progress_updated_at</c> leaves Continue Watching anchored to that instant; one without it but with
/// a <c>series_id</c> (a next-up card, also inside a combined Continue Watching row) leaves Next Up.
/// </summary>
public sealed record HomeDismissal(string Surface, string ItemId, DateTimeOffset? ProgressUpdatedAt, string? SeriesId)
{
    /// <summary>The dismissal for <paramref name="card"/>, or null when the card has no remove action.</summary>
    public static HomeDismissal? For(CatalogItemBase card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.ProgressUpdatedAt is { } updatedAt)
        {
            return new(HomeSurfaces.ContinueWatching, card.ContentId, updatedAt, null);
        }
        return string.IsNullOrEmpty(card.SeriesId) ? null : new(HomeSurfaces.NextUp, card.ContentId, null, card.SeriesId);
    }

    internal HomeDismissalRequest ToRequest() =>
        Surface == HomeSurfaces.ContinueWatching ? new(ProgressUpdatedAt, null) : new(null, SeriesId);
}

/// <summary><c>PUT /api/v2/home/dismissals/{surface}/{item_id}</c> body (schema <c>HomeDismissal</c>).</summary>
public sealed record HomeDismissalRequest(DateTimeOffset? ProgressUpdatedAt, string? SeriesId);
