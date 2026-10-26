using System.Globalization;
using Granary.Core.Api.Requests;

namespace Granary.Core.ViewModels;

/// <summary>
/// A title from TMDB worded as the web's title page words it (<c>ExternalTitleContent.tsx</c>, <c>lib/mediaRequests.ts</c>):
/// its seasons, what the library has of them, and which ones a request asks for unless the viewer picks.
/// </summary>
internal static class TitleText
{
    /// <summary>A series' air years ("2019–2024"), else the year.</summary>
    public static string? Years(RequestMediaDetail detail)
    {
        if (detail.MediaType == "series" && YearOf(detail.FirstAirDate) is { } first)
        {
            return YearOf(detail.LastAirDate) is { } last && last != first ? $"{first}–{last}" : first;
        }
        return detail.Year is int year and > 0 ? year.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>A series' first network, else the first production company.</summary>
    public static string? Studio(RequestMediaDetail detail) =>
        detail.MediaType == "series" && detail.Networks.Count > 0 ? detail.Networks[0]
        : detail.ProductionCompanies.Count > 0 ? detail.ProductionCompanies[0]
        : null;

    /// <summary>"Season 2: The Return", or "Season 2" when that is its name.</summary>
    public static string SeasonName(RequestMediaSeason season)
    {
        string number = $"Season {season.SeasonNumber.ToString(CultureInfo.InvariantCulture)}";
        return !string.IsNullOrWhiteSpace(season.Name) && season.Name != number ? $"{number}: {season.Name}" : number;
    }

    /// <summary>"2022 · 9 episodes", or "Not announced" before TMDB dates or fills the season.</summary>
    public static string SeasonMeta(RequestMediaSeason season)
    {
        var parts = new List<string>();
        if (YearOf(season.AirDate) is { } year)
        {
            parts.Add(year);
        }
        if (season.EpisodeCount > 0)
        {
            parts.Add($"{season.EpisodeCount.ToString(CultureInfo.InvariantCulture)} {(season.EpisodeCount == 1 ? "episode" : "episodes")}");
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "Not announced";
    }

    /// <summary>Where a season stands: "In library", "Requested", "Partly in library", "Not aired yet", "Not announced"; null once aired.</summary>
    public static string? SeasonStatus(RequestMediaSeason season, DateOnly today) =>
        season.Availability == "available" ? "In library"
        : season.Requested ? "Requested"
        : season.Availability == "partial" ? "Partly in library"
        : HasAired(season, today) ? null
        : !string.IsNullOrEmpty(season.AirDate) && season.EpisodeCount > 0 ? "Not aired yet"
        : "Not announced";

    /// <summary>Started airing by TMDB's dates, against today's UTC date as the server compares them.</summary>
    public static bool HasAired(RequestMediaSeason season, DateOnly today) =>
        season.EpisodeCount > 0 && DateOf(season.AirDate) is { } aired && aired <= today;

    /// <summary>A season a new request can still ask for: not complete in the library, not asked for already.</summary>
    public static bool Requestable(RequestMediaSeason season) => season.Availability != "available" && !season.Requested;

    /// <summary>What a series request asks for unless the viewer changes it: every aired season still missing.</summary>
    public static IReadOnlyList<int> DefaultSeasons(IEnumerable<RequestMediaSeason> seasons, DateOnly today) =>
        [.. seasons.Where(s => Requestable(s) && HasAired(s, today)).Select(s => s.SeasonNumber)];

    /// <summary>The newest regular season that aired, or the first announced one when none has; null when it cannot be requested.</summary>
    public static int? LatestSeason(IReadOnlyList<RequestMediaSeason> seasons, DateOnly today)
    {
        var regular = seasons.Where(s => s.SeasonNumber > 0).ToList();
        var aired = regular.Where(s => HasAired(s, today)).ToList();
        var latest = aired.Count > 0
            ? aired.MaxBy(s => s.SeasonNumber)
            : regular.Where(s => !string.IsNullOrEmpty(s.AirDate)).MinBy(s => s.SeasonNumber);
        return latest is not null && Requestable(latest) ? latest.SeasonNumber : null;
    }

    /// <summary>The requestable regular seasons that have not aired yet.</summary>
    public static IReadOnlyList<int> UpcomingSeasons(IEnumerable<RequestMediaSeason> seasons, DateOnly today) =>
        [.. seasons.Where(s => s.SeasonNumber > 0 && Requestable(s) && !HasAired(s, today)).Select(s => s.SeasonNumber)];

    /// <summary>Why no season can be picked, for a series the viewer cannot request now.</summary>
    public static string UnavailableReason(string? reason) => reason switch
    {
        "already_available" => "Every season is already in the library.",
        "already_requested" => "This series already has an open request.",
        _ => $"{RequestText.ReasonLabel(reason)}.",
    };

    /// <summary>"12.3K votes" (the web's compact number).</summary>
    public static string Votes(int count) => count switch
    {
        >= 1_000_000 => $"{Compact(count / 1_000_000.0)}M votes",
        >= 1_000 => $"{Compact(count / 1_000.0)}K votes",
        _ => $"{count.ToString(CultureInfo.InvariantCulture)} votes",
    };

    private static string Compact(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);

    private static string? YearOf(string? date) => date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out _)
        ? date[..4]
        : null;

    private static DateOnly? DateOf(string? date) =>
        date is { Length: >= 10 } && DateOnly.TryParseExact(date[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
}
