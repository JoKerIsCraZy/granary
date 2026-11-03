using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels;

/// <summary>
/// A sort the library grid offers, with the order it starts in when picked, as the web's <c>lib/querySortOptions.ts</c>
/// has them.
/// </summary>
/// <param name="Field">A server sort field (<see cref="CatalogSortFields"/>).</param>
public sealed record LibrarySortOption(string Field, string Label, bool StartsDescending)
{
    /// <summary>Title, A to Z: the server's own default order.</summary>
    public static LibrarySortOption Title { get; } = new(CatalogSortFields.Title, "Title", StartsDescending: false);

    private static readonly LibrarySortOption AddedAt = new(CatalogSortFields.AddedAt, "Date Added", StartsDescending: true);
    private static readonly LibrarySortOption ReleaseDate = new(CatalogSortFields.ReleaseDate, "Release Date", StartsDescending: true);
    private static readonly LibrarySortOption Year = new(CatalogSortFields.Year, "Year", StartsDescending: true);
    private static readonly LibrarySortOption Runtime = new(CatalogSortFields.Runtime, "Duration", StartsDescending: true);
    private static readonly LibrarySortOption RatingImdb = new(CatalogSortFields.RatingImdb, "IMDb Rating", StartsDescending: true);

    /// <summary>Every sort, in the web's order.</summary>
    public static IReadOnlyList<LibrarySortOption> All { get; } =
    [
        Title,
        AddedAt,
        ReleaseDate,
        new(CatalogSortFields.LastAirDate, "Latest Episode Air Date", StartsDescending: true) { IsSeriesOnly = true },
        new(CatalogSortFields.LatestEpisodeAdded, "Latest Episode Added", StartsDescending: true) { IsSeriesOnly = true },
        Year,
        new(CatalogSortFields.ContentRating, "Content Rating", StartsDescending: false),
        Runtime,
        RatingImdb,
        new(CatalogSortFields.RatingTmdb, "TMDB Rating", StartsDescending: true),
        new(CatalogSortFields.RatingRtCritic, "RT Critic Rating", StartsDescending: true) { RatingSource = "rt_critic" },
        new(CatalogSortFields.RatingRtAudience, "RT Audience Rating", StartsDescending: true) { RatingSource = "rt_audience" },
        new(CatalogSortFields.Resolution, "Resolution", StartsDescending: true),
        new(CatalogSortFields.Bitrate, "Bitrate", StartsDescending: true),
        new(CatalogSortFields.Progress, "Progress", StartsDescending: true),
        new(CatalogSortFields.DateViewed, "Date Viewed", StartsDescending: true),
        new(CatalogSortFields.Plays, "Plays", StartsDescending: true),
    ];

    /// <summary>The sorts Favorites, Watchlist, History and a collection offer beside the list's own order.</summary>
    public static IReadOnlyList<LibrarySortOption> ForLists { get; } = [Title, AddedAt, ReleaseDate, Year, Runtime, RatingImdb];

    /// <summary>
    /// Latest Episode Air Date and Latest Episode Added sort only shows, so a series library alone offers them, as the web's
    /// sorts for a library type.
    /// </summary>
    public bool IsSeriesOnly { get; init; }

    /// <summary>The rating source a rating sort orders by; it is offered only while the server shows that source.</summary>
    public string? RatingSource { get; init; }

    public bool IsTitle => Field == CatalogSortFields.Title;

    /// <summary>
    /// The sorts of a library of <paramref name="type"/> (<see cref="CatalogQuery.Type"/>; null for a mixed one), rating
    /// sorts only for <paramref name="shownRatingSources"/>. The sort <paramref name="keep"/> stays offered with a hidden
    /// rating, as the web keeps a chosen sort, so a remembered order does not change under the viewer.
    /// </summary>
    public static IReadOnlyList<LibrarySortOption> For(string? type, IReadOnlySet<string> shownRatingSources, string? keep = null)
    {
        ArgumentNullException.ThrowIfNull(shownRatingSources);
        bool isSeries = type == MediaScopes.Series;
        return [.. All.Where(o => (isSeries || !o.IsSeriesOnly)
            && (o.RatingSource is not { } source || shownRatingSources.Contains(source) || o.Field == keep))];
    }
}
