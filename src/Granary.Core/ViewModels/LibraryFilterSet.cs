using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels;

/// <summary>The Watch Status filter, as the web's filter sheet offers it.</summary>
public enum LibraryWatchStatus
{
    Any,
    Unwatched,
    InProgress,
    Watched,
}

/// <summary>
/// The filters applied to a library's grid or a list: what the Filters flyout commits and <see cref="LibraryBrowseState"/>
/// keeps. Values are the ones the server's facets name (genres, ratings, language codes); a decade is its first year.
/// </summary>
public sealed record LibraryFilterSet
{
    public const int MinimumYear = 1;
    public const int MaximumYear = 9999;

    // The oldest decade the flyout offers.
    private const int FirstDecade = 1900;

    public static LibraryFilterSet None { get; } = new();

    /// <summary>A title matches any of them.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>First years (1990 for the 1990s); a title matches any of them.</summary>
    public IReadOnlyList<int> Decades { get; init; } = [];

    public int? YearFrom { get; init; }

    public int? YearTo { get; init; }

    /// <summary>A title matches any of them.</summary>
    public IReadOnlyList<string> ContentRatings { get; init; } = [];

    public bool FourK { get; init; }

    public bool Hdr { get; init; }

    public bool DolbyVision { get; init; }

    /// <summary>ISO 639-1 codes; a title matches any of them.</summary>
    public IReadOnlyList<string> OriginalLanguages { get; init; } = [];

    /// <summary>Lowercase BCP 47 tags of the files' audio tracks; a title matches any of them.</summary>
    public IReadOnlyList<string> AudioLanguages { get; init; } = [];

    public IReadOnlyList<string> SubtitleLanguages { get; init; } = [];

    public LibraryWatchStatus WatchStatus { get; init; }

    /// <summary>The count on the Filters pill: one per genre, decade, rating, language and video quality, one for the year range and one for a watch status.</summary>
    public int Count => Genres.Count + Decades.Count + ContentRatings.Count + OriginalLanguages.Count + AudioLanguages.Count + SubtitleLanguages.Count
        + (FourK ? 1 : 0) + (Hdr ? 1 : 0) + (DolbyVision ? 1 : 0)
        + (YearFrom is null && YearTo is null ? 0 : 1)
        + (WatchStatus == LibraryWatchStatus.Any ? 0 : 1);

    /// <summary>The decades the flyout offers, newest first: from the one of <paramref name="year"/> back to the 1900s.</summary>
    public static IReadOnlyList<int> DecadesUntil(int year)
    {
        var decades = new List<int>();
        for (int decade = year / 10 * 10; decade >= FirstDecade; decade -= 10)
        {
            decades.Add(decade);
        }
        return decades;
    }

    /// <summary>
    /// The query with these filters: the years and ratings as the listing's simple filters, everything else as rule groups
    /// (<see cref="LibraryFilterRules"/>), which take it to <c>POST /api/v2/catalog/query</c>.
    /// </summary>
    public CatalogQuery ApplyTo(CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rules = LibraryFilterRules.For(this);
        return query with
        {
            YearMin = YearFrom,
            YearMax = YearTo,
            ContentRatings = ContentRatings.Count > 0 ? ContentRatings : null,
            Rules = rules.Count > 0 ? rules : null,
        };
    }

    /// <summary>
    /// The filters as Apply takes them, so a damaged file can't make a query the server refuses: blanks and repeats left
    /// out, the years within range and in order, only decades that are decades.
    /// </summary>
    public LibraryFilterSet Normalized()
    {
        int? from = Year(YearFrom);
        int? to = Year(YearTo);
        if (from > to)
        {
            (from, to) = (to, from);
        }
        return this with
        {
            Genres = Values(Genres),
            Decades = [.. Decades.Where(d => d >= MinimumYear && d % 10 == 0 && d + 9 <= MaximumYear).Distinct()],
            YearFrom = from,
            YearTo = to,
            ContentRatings = Values(ContentRatings),
            OriginalLanguages = Values(OriginalLanguages),
            AudioLanguages = Values(AudioLanguages),
            SubtitleLanguages = Values(SubtitleLanguages),
            WatchStatus = Enum.IsDefined(WatchStatus) ? WatchStatus : LibraryWatchStatus.Any,
        };
    }

    public bool Equals(LibraryFilterSet? other) => other is not null
        && Genres.SequenceEqual(other.Genres) && Decades.SequenceEqual(other.Decades) && YearFrom == other.YearFrom && YearTo == other.YearTo
        && ContentRatings.SequenceEqual(other.ContentRatings) && FourK == other.FourK && Hdr == other.Hdr && DolbyVision == other.DolbyVision
        && OriginalLanguages.SequenceEqual(other.OriginalLanguages) && AudioLanguages.SequenceEqual(other.AudioLanguages)
        && SubtitleLanguages.SequenceEqual(other.SubtitleLanguages) && WatchStatus == other.WatchStatus;

    public override int GetHashCode() => HashCode.Combine(Count, YearFrom, YearTo, FourK, Hdr, DolbyVision, WatchStatus);

    private static string[] Values(IReadOnlyList<string> values) =>
        [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal)];

    // None when it isn't a year at all, else within the years a query takes.
    private static int? Year(int? value) => value is > 0 ? Math.Clamp(value.Value, MinimumYear, MaximumYear) : null;
}
