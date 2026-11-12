using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>
/// The Calendar page's words, as the web's <c>lib/calendarWeek.ts</c> and <c>lib/upcomingEventPresentation.ts</c> put them
/// ("Oct 5 – 11, 2026", "Monday, October 5th", "S2 · E1 - Hello"); dates in English like the rest of the app, air times
/// in the viewer's clock format.
/// </summary>
internal static class CalendarText
{
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    /// <summary>The Monday of the week <paramref name="day"/> is in.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>"Oct 5 – 11, 2026", or "Sep 28 – Oct 4, 2026" across two months.</summary>
    public static string WeekRange(DateOnly start)
    {
        var end = start.AddDays(6);
        return start.Month == end.Month && start.Year == end.Year
            ? $"{start.ToString("MMM d", English)} – {end.Day}, {start.Year}"
            : $"{start.ToString("MMM d", English)} – {end.ToString("MMM d, yyyy", English)}";
    }

    /// <summary>"Monday, October 5th".</summary>
    public static string DayHeading(DateOnly day) => $"{day.ToString("dddd, MMMM", English)} {Ordinal(day.Day)}";

    /// <summary>"Mon".</summary>
    public static string ShortDay(DateOnly day) => day.ToString("ddd", English);

    /// <summary>"Thu, Nov 19".</summary>
    public static string ShortDate(DateOnly day) => day.ToString("ddd, MMM d", English);

    /// <summary>"November 19".</summary>
    public static string MonthDay(DateOnly day) => day.ToString("MMMM d", English);

    /// <summary>"S2 · E1 - Hello" or "Season 2 · Hello" for an episode, "Movie" for a movie.</summary>
    public static string Subtitle(CalendarEvent airing)
    {
        if (airing.Type is "episode" or "season_premiere" && airing.SeasonNumber is { } season)
        {
            string title = airing.EpisodeTitle is { Length: > 0 } name ? name : "";
            if (airing.EpisodeNumber is { } episode)
            {
                // The numbers of a code stay in Latin digits in every language, as the web's codeNumber.
                string s = season.ToString(English), e = episode.ToString(English);
                return title.Length > 0
                    ? AppText.Get("web.calendar.event.episodeTitled", ("season", s), ("episode", e), ("title", title))
                    : AppText.Get("web.calendar.event.episode", ("season", s), ("episode", e));
            }
            return title.Length > 0
                ? AppText.Get("web.calendar.event.seasonTitled", ("season", season), ("title", title))
                : AppText.Get("web.calendar.event.season", ("season", season));
        }
        return airing.Type == "movie" ? AppText.Get("web.calendar.event.movie") : "";
    }

    /// <summary>The airing's time on the viewer's clock, or the network's wall-clock time; null when neither is known.</summary>
    public static string? AirTime(CalendarEvent airing, TimeZoneInfo zone)
    {
        if (airing.AirAt is { } instant)
        {
            return TimeZoneInfo.ConvertTime(instant, zone).ToString("t", CultureInfo.CurrentCulture);
        }
        return TimeOnly.TryParseExact(airing.AirTime, "HH:mm", English, DateTimeStyles.None, out var time)
            ? time.ToString("t", CultureInfo.CurrentCulture)
            : null;
    }

    /// <summary>"PREMIERE", "NEW SEASON", "FINALE": the web's badges (lib/upcomingEventPresentation.ts), in capitals as its CSS shows them.</summary>
    public static string? Badge(IReadOnlyList<string> badges) => badges.FirstOrDefault() switch
    {
        null => null,
        "series_premiere" => AppText.Upper(AppText.Get("web.calendar.event.badge.seriesPremiere")),
        "season_premiere" => AppText.Upper(AppText.Get("web.calendar.event.badge.seasonPremiere")),
        "finale" => AppText.Upper(AppText.Get("web.calendar.event.badge.finale")),
        "digital" => AppText.Upper(AppText.Get("web.calendar.event.badge.digital")),
        "physical" => AppText.Upper(AppText.Get("web.calendar.event.badge.physical")),
        var other => other.ToUpperInvariant(),
    };

    /// <summary>
    /// The zone's IANA name, which the server reckons days in. A zone Windows knows no name for goes by its offset now
    /// (<c>Etc/GMT-2</c> is two hours ahead of UTC); null (UTC) when that is not a whole number of hours.
    /// </summary>
    public static string? IanaZone(TimeZoneInfo zone, DateTimeOffset now)
    {
        if (zone.HasIanaId)
        {
            return zone.Id;
        }
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out string? iana))
        {
            return iana;
        }
        var offset = zone.GetUtcOffset(now);
        if (offset.Ticks % TimeSpan.TicksPerHour != 0)
        {
            return null;
        }
        int hours = (int)offset.TotalHours;
        return hours == 0 ? "Etc/UTC" : $"Etc/GMT{(hours > 0 ? "-" : "+")}{Math.Abs(hours)}";
    }

    private static string Ordinal(int day) => day + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch
    {
        1 => "st",
        2 => "nd",
        3 => "rd",
        _ => "th",
    });
}
