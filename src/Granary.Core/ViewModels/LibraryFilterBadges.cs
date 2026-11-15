using System.Globalization;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>What one badge under the filter bar stands for.</summary>
public enum LibraryFilterBadgeKind
{
    Genre,
    Decade,
    Year,
    ContentRating,
    OriginalLanguage,
    AudioLanguage,
    SubtitleLanguage,
    WatchStatus,
    FourK,
    Hdr,
    DolbyVision,
}

/// <summary>One applied filter as a badge under the filter bar, removed on its own with its ✕.</summary>
/// <param name="Value">The genre, rating, language code or decade it removes; empty for the years, the watch status and a quality.</param>
public sealed record LibraryFilterBadge(LibraryFilterBadgeKind Kind, string Value, string Label)
{
    /// <summary>The ✕'s name for screen readers, as the web's.</summary>
    public string RemoveName => AppText.Get("web.catalog.filters.removeBadge", ("filter", Label));
}

/// <summary>
/// The applied filters as the web's badges (<c>components/catalog/catalogFilterBadges.ts</c>), with its labels and in its
/// order: "Genre: Crime", the year range as one, "Rated: R", "Language: French", "Watch: in progress", "4K", "HDR", "DOVI".
/// The facets the web has no badge for are worded the same way and stand where the flyout has them: "Decade: 1990s" after
/// the genres, "Audio: German" and "Subtitles: English" after the original language.
/// </summary>
public static class LibraryFilterBadges
{
    public static IReadOnlyList<LibraryFilterBadge> For(LibraryFilterSet filters, Func<string, string> languageName)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(languageName);
        var badges = new List<LibraryFilterBadge>();
        badges.AddRange(filters.Genres.Select(g =>
            new LibraryFilterBadge(LibraryFilterBadgeKind.Genre, g, AppText.Get("web.catalog.filters.badgeGenre", ("genre", g)))));
        badges.AddRange(filters.Decades.Select(d => new LibraryFilterBadge(LibraryFilterBadgeKind.Decade, Invariant(d),
            AppText.Get("granary.libraryFilters.badgeDecade", ("decade", Invariant(d))))));
        if (YearLabel(filters.YearFrom, filters.YearTo) is { } years)
        {
            badges.Add(new LibraryFilterBadge(LibraryFilterBadgeKind.Year, "", years));
        }
        badges.AddRange(filters.ContentRatings.Select(r => new LibraryFilterBadge(LibraryFilterBadgeKind.ContentRating, r,
            AppText.Get("web.catalog.filters.badgeContentRating", ("rating", r)))));
        badges.AddRange(filters.OriginalLanguages.Select(l => new LibraryFilterBadge(LibraryFilterBadgeKind.OriginalLanguage, l,
            AppText.Get("web.catalog.filters.badgeLanguage", ("language", languageName(l))))));
        badges.AddRange(filters.AudioLanguages.Select(l => new LibraryFilterBadge(LibraryFilterBadgeKind.AudioLanguage, l,
            AppText.Get("granary.libraryFilters.badgeAudio", ("language", languageName(l))))));
        badges.AddRange(filters.SubtitleLanguages.Select(l => new LibraryFilterBadge(LibraryFilterBadgeKind.SubtitleLanguage, l,
            AppText.Get("granary.libraryFilters.badgeSubtitles", ("language", languageName(l))))));
        if (WatchLabel(filters.WatchStatus) is { } watch)
        {
            badges.Add(new LibraryFilterBadge(LibraryFilterBadgeKind.WatchStatus, "", watch));
        }
        if (filters.FourK)
        {
            badges.Add(new LibraryFilterBadge(LibraryFilterBadgeKind.FourK, "", "4K"));
        }
        if (filters.Hdr)
        {
            badges.Add(new LibraryFilterBadge(LibraryFilterBadgeKind.Hdr, "", "HDR"));
        }
        if (filters.DolbyVision)
        {
            badges.Add(new LibraryFilterBadge(LibraryFilterBadgeKind.DolbyVision, "", "DOVI"));
        }
        return badges;
    }

    /// <summary>The filters without the badge's piece; one already gone changes nothing.</summary>
    public static LibraryFilterSet Remove(LibraryFilterSet filters, LibraryFilterBadge badge)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(badge);
        return badge.Kind switch
        {
            LibraryFilterBadgeKind.Genre => filters with { Genres = Without(filters.Genres, badge.Value) },
            LibraryFilterBadgeKind.Decade => filters with { Decades = [.. filters.Decades.Where(d => Invariant(d) != badge.Value)] },
            LibraryFilterBadgeKind.Year => filters with { YearFrom = null, YearTo = null },
            LibraryFilterBadgeKind.ContentRating => filters with { ContentRatings = Without(filters.ContentRatings, badge.Value) },
            LibraryFilterBadgeKind.OriginalLanguage => filters with { OriginalLanguages = Without(filters.OriginalLanguages, badge.Value) },
            LibraryFilterBadgeKind.AudioLanguage => filters with { AudioLanguages = Without(filters.AudioLanguages, badge.Value) },
            LibraryFilterBadgeKind.SubtitleLanguage => filters with { SubtitleLanguages = Without(filters.SubtitleLanguages, badge.Value) },
            LibraryFilterBadgeKind.WatchStatus => filters with { WatchStatus = LibraryWatchStatus.Any },
            LibraryFilterBadgeKind.FourK => filters with { FourK = false },
            LibraryFilterBadgeKind.Hdr => filters with { Hdr = false },
            LibraryFilterBadgeKind.DolbyVision => filters with { DolbyVision = false },
            _ => filters,
        };
    }

    // "Year: 1990–1999", "Year: >= 1990", "Year: <= 1999".
    private static string? YearLabel(int? from, int? to) => (from, to) switch
    {
        ({ } first, { } last) => AppText.Get("web.catalog.filters.badgeYearRange", ("from", Invariant(first)), ("to", Invariant(last))),
        ({ } first, null) => AppText.Get("web.catalog.filters.badgeYearFrom", ("year", Invariant(first))),
        (null, { } last) => AppText.Get("web.catalog.filters.badgeYearTo", ("year", Invariant(last))),
        _ => null,
    };

    // The web lower-cases the status and spells in_progress with a space.
    private static string? WatchLabel(LibraryWatchStatus status) => status switch
    {
        LibraryWatchStatus.Unwatched => AppText.Get("web.catalog.filters.badgeWatch", ("status", "unwatched")),
        LibraryWatchStatus.InProgress => AppText.Get("web.catalog.filters.badgeWatch", ("status", "in_progress")),
        LibraryWatchStatus.Watched => AppText.Get("web.catalog.filters.badgeWatch", ("status", "watched")),
        _ => null,
    };

    private static string[] Without(IReadOnlyList<string> values, string value) => [.. values.Where(v => v != value)];

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
