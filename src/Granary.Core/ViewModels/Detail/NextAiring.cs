using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// When a series' next episode airs, as Silo's Apple app words a monitored series' next download
/// (<c>AutoDownloadSchedule.upcoming</c>, <c>AutoDownloadRules.nextEpisode</c>, <c>.statusLine</c> and <c>.day</c>):
/// "Episode 6 on Thursday", "Season 4 starts tomorrow".
/// </summary>
internal static class NextAiring
{
    /// <summary>
    /// The first airing still to come, from any season but the specials: one with its time until that time, one with only
    /// its date from the day after on, since nobody can say whether today's has aired. The earliest moment goes first (a
    /// date alone is the start of its day in the calendar's reckoning), then the lower season and episode.
    /// </summary>
    public static EpisodeAiring? Find(IEnumerable<EpisodeAiring> airings, CalendarNow now) => airings
        .Select(airing => (Airing: airing, Moment: MomentOf(airing, now)))
        .Where(entry => entry.Airing.SeasonNumber > 0 && entry.Moment is not null)
        .OrderBy(entry => entry.Moment)
        .ThenBy(entry => entry.Airing.SeasonNumber)
        .ThenBy(entry => entry.Airing.EpisodeNumber)
        .Select(entry => entry.Airing)
        .FirstOrDefault();

    // When an airing still to come airs, as Apple's AirDateParser reads it; null for one that may have aired, or has no date.
    private static DateTimeOffset? MomentOf(EpisodeAiring airing, CalendarNow now)
    {
        if (airing.AirAt is { } at)
        {
            return at > now.Now ? at : null;
        }
        return SeasonGaps.Day(airing, now.Zone) is { } day && day > now.Today ? now.StartOf(day) : null;
    }

    /// <summary>
    /// Apple's <c>downloads.monitor.next.*</c> words: a season's first episode starts the season, any other is named by its
    /// number; the day is today, tomorrow, the weekday within a week, else the month and day, in the viewer's zone and the
    /// format locale.
    /// </summary>
    public static string Text(EpisodeAiring airing, CalendarNow now)
    {
        // An instant falls on the viewer's day; a date alone is the day the calendar listed it under, in the zone it reckons
        // in, which is the viewer's unless the zone has no name the server knows.
        var today = airing.AirAt is null ? now.Today : now.LocalToday;
        var day = SeasonGaps.Day(airing, now.Zone) ?? today;
        int days = day.DayNumber - today.DayNumber;
        // A number in a message is Apple's LocaleFormat.integer: the language's digits, never grouped.
        int season = airing.SeasonNumber, episode = airing.EpisodeNumber;
        string date = days < 7 ? CalendarText.Weekday(day) : CalendarText.ShortMonthDay(day);
        return (days, episode == 1) switch
        {
            (<= 0, true) => AppText.Get("granary.detail.nextAiring.seasonStartsToday", ("season", season)),
            (<= 0, false) => AppText.Get("granary.detail.nextAiring.episodeToday", ("episode", episode)),
            (1, true) => AppText.Get("granary.detail.nextAiring.seasonStartsTomorrow", ("season", season)),
            (1, false) => AppText.Get("granary.detail.nextAiring.episodeTomorrow", ("episode", episode)),
            (_, true) => AppText.Get("granary.detail.nextAiring.seasonStartsOn", ("season", season), ("date", date)),
            _ => AppText.Get("granary.detail.nextAiring.episodeOn", ("episode", episode), ("date", date)),
        };
    }
}
