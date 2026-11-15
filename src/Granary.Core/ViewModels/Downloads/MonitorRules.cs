using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>An episode of a series that airs soon, from the airing calendar.</summary>
public sealed record UpcomingEpisode(string ContentId, string SeriesId, int SeasonNumber, int EpisodeNumber, string? Title, DateTimeOffset AirDate);

/// <summary>Where one of a series' downloads on its way to this PC stands, for a monitor's status.</summary>
public enum MonitorPhase
{
    Downloading,
    Preparing,
    Queued,

    /// <summary>The connection is metered and Settings → Downloads does not allow it: the PC's way of waiting for Wi-Fi.</summary>
    WaitingForUnmetered,

    /// <summary>Its monitor's storage limit holds it.</summary>
    SeriesLimit,

    /// <summary>Granary's storage limit for every download on this PC (Settings → Downloads) holds it.</summary>
    DownloadsLimit,
}

/// <summary>One of a monitored series' downloads still on its way: its numbers, where it stands, and how much has come.</summary>
public sealed record MonitorActivity(int? SeasonNumber, int? EpisodeNumber, MonitorPhase Phase, double Fraction);

/// <summary>What a monitored series does next, as the Apple app's Monitored list and series page say it (<c>AutoDownloadStatus</c>).</summary>
public abstract record MonitorStatus
{
    private MonitorStatus()
    {
    }

    public sealed record Paused : MonitorStatus;

    /// <summary>Its next episode would pass a storage limit of <paramref name="MaxBytes"/>.</summary>
    public sealed record StorageLimit(long MaxBytes) : MonitorStatus;

    public sealed record Downloading(int? EpisodeNumber, double Fraction) : MonitorStatus;

    public sealed record Waiting(int? EpisodeNumber, MonitorPhase Phase) : MonitorStatus;

    /// <summary>Nothing is on its way; the next episode it takes airs then.</summary>
    public sealed record Next(UpcomingEpisode Episode) : MonitorStatus;

    public sealed record UpToDate : MonitorStatus;

    /// <summary>Nothing is on its way, and the airing calendar has not been read: whether anything is due is not known.</summary>
    public sealed record Monitoring : MonitorStatus;
}

/// <summary>
/// A monitor's rule and status in Silo's Apple app's words and translations (<c>AutoDownloadStatus.swift</c>,
/// <c>AutoDownloadRules</c>, and <c>SeriesDownloadSheet.swift</c>), as Granary's own <c>granary.monitor.*</c> texts: the web has
/// none. "This PC" stands where the Apple app names its device. Numbers come in the language's digits, never grouped, as
/// Apple's <c>LocaleFormat.integer</c>, and days in the format locale.
/// </summary>
public static class MonitorRules
{
    /// <summary>The row of another client's specific seasons (<c>downloads.seriesSheet.custom</c>).</summary>
    public static string Custom => AppText.Get("granary.monitor.mode.custom");

    /// <summary>A mode's row in the download dialog: "All episodes", "Future episodes", "Last season", "Custom".</summary>
    public static string ModeName(string mode) => mode switch
    {
        SubscriptionModes.All => AppText.Get("granary.monitor.mode.all"),
        SubscriptionModes.Future => AppText.Get("granary.monitor.mode.future"),
        SubscriptionModes.LatestSeason => AppText.Get("granary.monitor.mode.lastSeason"),
        _ => Custom,
    };

    /// <summary>
    /// A mode's line under its row (downloads.seriesSheet.rule*): "Only episodes that air from now on", "Season 3 and anything
    /// newer"; a custom monitor's seasons.
    /// </summary>
    public static string ModeDetail(string mode, int? latestSeason, IReadOnlyCollection<int> seasons) => mode switch
    {
        SubscriptionModes.All => AppText.Get("granary.monitor.ruleDetail.all"),
        SubscriptionModes.Future => AppText.Get("granary.monitor.ruleDetail.future"),
        SubscriptionModes.LatestSeason => latestSeason is { } season
            ? AppText.Get("granary.monitor.ruleDetail.latestFrom", ("season", season))
            : AppText.Get("granary.monitor.ruleDetail.latest"),
        _ => SeasonList(seasons),
    };

    /// <summary>What a monitor takes, "Last season · Season 3 on", and its quality when it is not original: "Future episodes · 10 Mbps".</summary>
    public static string Summary(DownloadSubscription monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        string rule = monitor.Mode switch
        {
            SubscriptionModes.All => AppText.Get("granary.monitor.rule.all"),
            SubscriptionModes.Future => AppText.Get("granary.monitor.rule.future"),
            SubscriptionModes.LatestSeason => monitor.TargetSeason is { } season
                ? AppText.Get("granary.monitor.rule.lastSeasonFrom", ("season", season))
                : AppText.Get("granary.monitor.rule.lastSeason"),
            SubscriptionModes.SpecificSeasons => SeasonList(monitor.SeasonNumbers),
            _ => AppText.Get("granary.monitor.rule.unknown"),
        };
        return monitor.Quality is { Length: > 0 } quality && quality != DownloadQualities.Original
            ? $"{rule} · {DownloadText.QualityLabel(new DownloadQualityOption(quality, null, null))}"
            : rule;
    }

    /// <summary>"Season 4", "Seasons 2 and 3", "Seasons 1, 2 and 4", listed as the language lists names; "No seasons".</summary>
    public static string SeasonList(IReadOnlyCollection<int> seasons)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        var sorted = seasons.Distinct().Order().ToList();
        return sorted.Count switch
        {
            0 => AppText.Get("granary.monitor.seasons.none"),
            1 => AppText.Get("granary.monitor.seasons.single", ("season", sorted[0])),
            _ => AppText.Get("granary.monitor.seasons.list", ("seasons", TextList.And([.. sorted.Select(season => NumberText.Digits(season))]))),
        };
    }

    /// <summary>"10 GB" for whole gigabytes, as the limits are chosen; another client's limit as its size, "1.5 GB".</summary>
    public static string LimitText(long maxBytes) =>
        maxBytes > 0 && maxBytes % DownloadPreferenceLimits.BytesPerGb == 0
            ? AppText.Get("web.common.format.size.gb", ("size", maxBytes / DownloadPreferenceLimits.BytesPerGb))
            : PrePlayText.FileSize(maxBytes);

    /// <summary>Whether the monitor's rule takes an episode of <paramref name="seasonNumber"/>; every mode takes what has not aired unless it names seasons.</summary>
    public static bool Covers(string mode, int? targetSeason, IReadOnlyCollection<int> seasons, int seasonNumber) => mode switch
    {
        SubscriptionModes.All or SubscriptionModes.Future => true,
        SubscriptionModes.LatestSeason => seasonNumber >= (targetSeason ?? 0),
        SubscriptionModes.SpecificSeasons => seasons.Contains(seasonNumber),
        _ => false,
    };

    /// <summary>The first episode the rule takes that has not aired and is not on its way, by air date, then season and number.</summary>
    public static UpcomingEpisode? NextEpisode(string mode, int? targetSeason, IReadOnlyCollection<int> seasons, IEnumerable<UpcomingEpisode> upcoming,
        IReadOnlyCollection<string> knownEpisodeIds, DateTimeOffset now) =>
        upcoming.Where(e => e.AirDate > now && !knownEpisodeIds.Contains(e.ContentId) && Covers(mode, targetSeason, seasons, e.SeasonNumber))
            .OrderBy(e => e.AirDate).ThenBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .FirstOrDefault();

    /// <summary>
    /// A paused monitor says so first; then a download a storage limit holds, then one under way, then one waiting; only a
    /// series with nothing on its way names its next airing, and before the calendar was read it only says it monitors.
    /// </summary>
    public static MonitorStatus StatusOf(DownloadSubscription monitor, IReadOnlyCollection<MonitorActivity> activity, IEnumerable<UpcomingEpisode> upcoming,
        IReadOnlyCollection<string> knownEpisodeIds, bool scheduleKnown, DateTimeOffset now, long? downloadsLimit = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(activity);
        if (!monitor.Active)
        {
            return new MonitorStatus.Paused();
        }
        var ordered = activity.OrderBy(a => a.SeasonNumber ?? 0).ThenBy(a => a.EpisodeNumber ?? 0).ToList();
        // The first episode a limit holds names that limit: its monitor's, or Granary's for every download.
        if (ordered.FirstOrDefault(a => a.Phase is MonitorPhase.SeriesLimit or MonitorPhase.DownloadsLimit) is { } held)
        {
            return new MonitorStatus.StorageLimit(held.Phase == MonitorPhase.SeriesLimit ? monitor.MaxStorageBytes : downloadsLimit ?? 0);
        }
        if (ordered.FirstOrDefault(a => a.Phase == MonitorPhase.Downloading) is { } downloading)
        {
            return new MonitorStatus.Downloading(downloading.EpisodeNumber, downloading.Fraction);
        }
        if (ordered.FirstOrDefault() is { } first)
        {
            return new MonitorStatus.Waiting(first.EpisodeNumber, first.Phase);
        }
        if (NextEpisode(monitor.Mode, monitor.TargetSeason, monitor.SeasonNumbers, upcoming, knownEpisodeIds, now) is { } next)
        {
            return new MonitorStatus.Next(next);
        }
        return scheduleKnown ? new MonitorStatus.UpToDate() : new MonitorStatus.Monitoring();
    }

    /// <summary>A Monitored row's line: "Episode 6 on Thursday", "Downloading Episode 6 · 42%", "Up to date".</summary>
    public static string StatusLine(MonitorStatus status, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status switch
        {
            MonitorStatus.Paused => AppText.Get("granary.monitor.status.paused"),
            MonitorStatus.StorageLimit limit => AppText.Get("granary.monitor.status.outOfSpace", ("limit", LimitText(limit.MaxBytes))),
            MonitorStatus.Downloading { EpisodeNumber: { } episode } downloading =>
                AppText.Get("granary.monitor.status.downloading", ("episode", episode), ("percent", Percent(downloading.Fraction))),
            MonitorStatus.Downloading downloading =>
                AppText.Get("granary.monitor.status.downloadingUnnamed", ("percent", Percent(downloading.Fraction))),
            MonitorStatus.Waiting waiting => WaitingLine(waiting.EpisodeNumber, waiting.Phase),
            MonitorStatus.Next { Episode: var next } => NextLine(next, now, zone),
            MonitorStatus.UpToDate => AppText.Get("granary.monitor.status.upToDate"),
            _ => AppText.Get("granary.monitor.status.monitoring"),
        };
    }

    /// <summary>The download dialog's line for the next airing: "Episode 6 downloads Thursday", "Season 2 downloads tomorrow".</summary>
    public static string Headline(MonitorStatus status, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status is MonitorStatus.Paused)
        {
            return AppText.Get("granary.monitor.headline.paused");
        }
        if (status is not MonitorStatus.Next { Episode: var next })
        {
            return StatusLine(status, now, zone);
        }
        // A season's first episode downloads the season.
        int season = next.SeasonNumber, episode = next.EpisodeNumber;
        return (Day(next.AirDate, now, zone), episode == 1) switch
        {
            (RelativeDay.Today, true) => AppText.Get("granary.monitor.headline.seasonToday", ("season", season)),
            (RelativeDay.Today, false) => AppText.Get("granary.monitor.headline.episodeToday", ("episode", episode)),
            (RelativeDay.Tomorrow, true) => AppText.Get("granary.monitor.headline.seasonTomorrow", ("season", season)),
            (RelativeDay.Tomorrow, false) => AppText.Get("granary.monitor.headline.episodeTomorrow", ("episode", episode)),
            (var day, true) => AppText.Get("granary.monitor.headline.seasonOn", ("season", season), ("date", DayText(next.AirDate, zone, day))),
            (var day, false) => AppText.Get("granary.monitor.headline.episodeOn", ("episode", episode), ("date", DayText(next.AirDate, zone, day))),
        };
    }

    // A season's first episode starts the season; any other is named by its number.
    private static string NextLine(UpcomingEpisode next, DateTimeOffset now, TimeZoneInfo zone)
    {
        int season = next.SeasonNumber, episode = next.EpisodeNumber;
        return (Day(next.AirDate, now, zone), episode == 1) switch
        {
            (RelativeDay.Today, true) => AppText.Get("granary.monitor.next.seasonStartsToday", ("season", season)),
            (RelativeDay.Today, false) => AppText.Get("granary.monitor.next.episodeToday", ("episode", episode)),
            (RelativeDay.Tomorrow, true) => AppText.Get("granary.monitor.next.seasonStartsTomorrow", ("season", season)),
            (RelativeDay.Tomorrow, false) => AppText.Get("granary.monitor.next.episodeTomorrow", ("episode", episode)),
            (var day, true) => AppText.Get("granary.monitor.next.seasonStartsOn", ("season", season), ("date", DayText(next.AirDate, zone, day))),
            (var day, false) => AppText.Get("granary.monitor.next.episodeOn", ("episode", episode), ("date", DayText(next.AirDate, zone, day))),
        };
    }

    // Where the Apple app waits for Wi-Fi, a PC waits for a connection Windows does not call metered: Granary's own words.
    private static string WaitingLine(int? episode, MonitorPhase phase)
    {
        if (episode is not { } number)
        {
            return phase switch
            {
                MonitorPhase.WaitingForUnmetered => AppText.Get("granary.monitor.waiting.unnamedUnmetered"),
                MonitorPhase.Preparing => AppText.Get("granary.monitor.waiting.unnamedPreparing"),
                _ => AppText.Get("granary.monitor.waiting.unnamedNext"),
            };
        }
        return phase switch
        {
            MonitorPhase.WaitingForUnmetered => AppText.Get("granary.monitor.waiting.unmetered", ("episode", number)),
            MonitorPhase.Preparing => AppText.Get("granary.monitor.waiting.preparing", ("episode", number)),
            _ => AppText.Get("granary.monitor.waiting.next", ("episode", number)),
        };
    }

    private enum RelativeDay
    {
        Today,
        Tomorrow,
        ThisWeek,
        Later,
    }

    // When an airing falls in the viewer's zone: today (or before), tomorrow, within the week, or later.
    private static RelativeDay Day(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        int days = day.DayNumber - today.DayNumber;
        return days <= 0 ? RelativeDay.Today : days == 1 ? RelativeDay.Tomorrow : days < 7 ? RelativeDay.ThisWeek : RelativeDay.Later;
    }

    // "Thursday" within the week, else "Nov 30", in the format locale.
    private static string DayText(DateTimeOffset at, TimeZoneInfo zone, RelativeDay day)
    {
        var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        return day == RelativeDay.ThisWeek ? CalendarText.Weekday(local) : CalendarText.ShortMonthDay(local);
    }

    private static int Percent(double fraction) => (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
}
