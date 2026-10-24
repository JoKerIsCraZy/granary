using System.Globalization;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// Detail page wording as Silo's web client has it (web/src/lib/mediaFormat.ts, web/src/pages/ItemDetail).
/// Chip texts are upper-cased by <see cref="DetailFacts"/>, not here.
/// </summary>
public static class DetailText
{
    /// <summary>"1h 49m", "52m" (web: formatRuntimeMinutes); null without a runtime.</summary>
    public static string? Runtime(int? minutes) => minutes switch
    {
        null or <= 0 => null,
        >= 60 => string.Create(CultureInfo.InvariantCulture, $"{minutes / 60}h {minutes % 60}m"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{minutes}m"),
    };

    /// <summary>The year of a <c>YYYY-MM-DD</c> date, or null when it has none.</summary>
    public static string? YearOf(string? isoDate) =>
        isoDate is { Length: >= 4 }
        && int.TryParse(isoDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year)
        && year > 0
            ? year.ToString(CultureInfo.InvariantCulture)
            : null;

    public static string? Year(int? year) => year is > 0 ? year.Value.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>"2021–2026" from a series' first and last air dates; a single year when both match or the end is unknown.</summary>
    public static string? AirYears(string? firstAirDate, string? lastAirDate, int? year)
    {
        string? first = YearOf(firstAirDate) ?? Year(year);
        if (first is null)
        {
            return null;
        }
        string? last = YearOf(lastAirDate);
        return last is not null && last != first ? $"{first}–{last}" : first;
    }

    /// <summary>"3 seasons", "1 episode".</summary>
    public static string Count(int count, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural)}");

    /// <summary>A server date (<c>2026-10-23</c>) as the web shows it: "23 Oct 2026".</summary>
    public static string? Date(string? isoDate) =>
        DateOnly.TryParseExact(isoDate?.Length >= 10 ? isoDate[..10] : isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : null;

    /// <summary>"S03E01 · Chapter 1", or the code alone for an episode without a title.</summary>
    public static string EpisodeTitle(int season, int episode, string? title)
    {
        string code = string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}");
        return string.IsNullOrWhiteSpace(title) ? code : $"{code} · {title.Trim()}";
    }

    /// <summary>"Season 3", or "Specials" for season 0.</summary>
    public static string SeasonLabel(int seasonNumber) =>
        seasonNumber == 0 ? "Specials" : string.Create(CultureInfo.InvariantCulture, $"Season {seasonNumber}");

    /// <summary>A season's own name when it says more than its number ("Staffel 3"), else <see cref="SeasonLabel"/> (web: getSeasonDisplayTitle).</summary>
    public static string SeasonDisplayTitle(int seasonNumber, string? title, bool isSpecials = false)
    {
        if (isSpecials || seasonNumber == 0)
        {
            return "Specials";
        }
        return string.IsNullOrWhiteSpace(title) ? SeasonLabel(seasonNumber) : title.Trim();
    }
}
