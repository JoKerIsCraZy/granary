using Granary.Core.Api.Catalog;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>
/// A sort the library grid offers, with the order it starts in when picked, as the web's <c>lib/querySortOptions.ts</c>
/// has them.
/// </summary>
/// <param name="Field">A server sort field (<see cref="CatalogSortFields"/>).</param>
public sealed record LibrarySortOption(string Field, bool StartsDescending)
{
    /// <summary>Title, A to Z: the server's own default order.</summary>
    public static LibrarySortOption Title { get; } = new(CatalogSortFields.Title, StartsDescending: false);

    private static readonly LibrarySortOption AddedAt = new(CatalogSortFields.AddedAt, StartsDescending: true);
    private static readonly LibrarySortOption ReleaseDate = new(CatalogSortFields.ReleaseDate, StartsDescending: true);
    private static readonly LibrarySortOption Year = new(CatalogSortFields.Year, StartsDescending: true);
    private static readonly LibrarySortOption Runtime = new(CatalogSortFields.Runtime, StartsDescending: true);
    private static readonly LibrarySortOption RatingImdb = new(CatalogSortFields.RatingImdb, StartsDescending: true);

    /// <summary>Every sort, in the web's order.</summary>
    public static IReadOnlyList<LibrarySortOption> All { get; } =
    [
        Title,
        AddedAt,
        ReleaseDate,
        new(CatalogSortFields.LastAirDate, StartsDescending: true) { IsSeriesOnly = true },
        new(CatalogSortFields.LatestEpisodeAdded, StartsDescending: true) { IsSeriesOnly = true },
        Year,
        new(CatalogSortFields.ContentRating, StartsDescending: false),
        Runtime,
        RatingImdb,
        new(CatalogSortFields.RatingTmdb, StartsDescending: true),
        new(CatalogSortFields.RatingRtCritic, StartsDescending: true) { RatingSource = "rt_critic" },
        new(CatalogSortFields.RatingRtAudience, StartsDescending: true) { RatingSource = "rt_audience" },
        new(CatalogSortFields.Resolution, StartsDescending: true),
        new(CatalogSortFields.Bitrate, StartsDescending: true),
        new(CatalogSortFields.Progress, StartsDescending: true),
        new(CatalogSortFields.DateViewed, StartsDescending: true),
        new(CatalogSortFields.Plays, StartsDescending: true),
    ];

    /// <summary>The sorts Favorites, Watchlist, History and a collection offer beside the list's own order.</summary>
    public static IReadOnlyList<LibrarySortOption> ForLists { get; } = [Title, AddedAt, ReleaseDate, Year, Runtime, RatingImdb];

    /// <summary>The sort's name in the menu, in the language on screen, as the web's labels (its labelKey).</summary>
    public string Label => Field switch
    {
        CatalogSortFields.Title => AppText.Get("catalog.sort.title"),
        CatalogSortFields.AddedAt => AppText.Get("catalog.sort.added"),
        CatalogSortFields.ReleaseDate => AppText.Get("catalog.sort.released"),
        CatalogSortFields.LastAirDate => AppText.Get("web.common.sort.latestEpisodeAirDate"),
        CatalogSortFields.LatestEpisodeAdded => AppText.Get("web.common.sort.latestEpisodeAdded"),
        CatalogSortFields.Year => AppText.Get("catalog.sort.year"),
        CatalogSortFields.ContentRating => AppText.Get("web.common.sort.contentRating"),
        CatalogSortFields.Runtime => AppText.Get("web.common.sort.duration"),
        CatalogSortFields.RatingImdb => AppText.Get("catalog.sort.imdbRating"),
        CatalogSortFields.RatingTmdb => AppText.Get("web.common.sort.tmdbRating"),
        CatalogSortFields.RatingRtCritic => AppText.Get("web.common.sort.rtCriticRating"),
        CatalogSortFields.RatingRtAudience => AppText.Get("web.common.sort.rtAudienceRating"),
        CatalogSortFields.Resolution => AppText.Get("catalog.sort.resolution"),
        CatalogSortFields.Bitrate => AppText.Get("web.common.sort.bitrate"),
        CatalogSortFields.Progress => AppText.Get("web.common.sort.progress"),
        CatalogSortFields.DateViewed => AppText.Get("web.common.sort.dateViewed"),
        CatalogSortFields.Plays => AppText.Get("web.common.sort.plays"),
        _ => Field,
    };

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
