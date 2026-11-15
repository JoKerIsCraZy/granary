using System.Globalization;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
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

    /// <summary>A storage limit holds it.</summary>
    StorageLimit,
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
/// A monitor's rule and status in Silo's Apple app's words (<c>AutoDownloadStatus.swift</c>, <c>AutoDownloadRules</c>, and
/// <c>SeriesDownloadSheet.swift</c>): the web has none. "This PC" stands where the Apple app names its device.
/// </summary>
public static class MonitorRules
{
    // The download dialog's rows (downloads.monitor.mode.*).
    public const string AllEpisodes = "All Episodes";
    public const string FutureEpisodes = "Future Episodes";
    public const string LastSeason = "Last Season";
    public const string Custom = "Custom";

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>A mode's row in the download dialog: "All Episodes", "Future Episodes", "Last Season", "Custom".</summary>
    public static string ModeName(string mode) => mode switch
    {
        SubscriptionModes.All => AllEpisodes,
        SubscriptionModes.Future => FutureEpisodes,
        SubscriptionModes.LatestSeason => LastSeason,
        _ => Custom,
    };

    /// <summary>
    /// A mode's line under its row (downloads.seriesSheet.rule*): "Only episodes that air from now on", "Season 3 and anything
    /// newer"; a custom monitor's seasons.
    /// </summary>
    public static string ModeDetail(string mode, int? latestSeason, IReadOnlyCollection<int> seasons) => mode switch
    {
        SubscriptionModes.All => "Download every episode now, then new ones as they air",
        SubscriptionModes.Future => "Only episodes that air from now on",
        SubscriptionModes.LatestSeason => latestSeason is { } season
            ? $"Season {Number(season)} and anything newer"
            : "The latest season and anything newer",
        _ => SeasonList(seasons),
    };

    /// <summary>What a monitor takes, "Last season · Season 3 on", and its quality when it is not original: "Future episodes · 10 Mbps".</summary>
    public static string Summary(DownloadSubscription monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        string rule = monitor.Mode switch
        {
            SubscriptionModes.All => "All episodes",
            SubscriptionModes.Future => "Future episodes",
            SubscriptionModes.LatestSeason => monitor.TargetSeason is { } season ? $"Last season · Season {Number(season)} on" : "Last season",
            SubscriptionModes.SpecificSeasons => SeasonList(monitor.SeasonNumbers),
            _ => "Monitored",
        };
        return monitor.Quality is { Length: > 0 } quality && quality != DownloadQualities.Original
            ? $"{rule} · {DownloadText.QualityLabel(new DownloadQualityOption(quality, null, null))}"
            : rule;
    }

    /// <summary>"Season 4", "Seasons 2 and 3", "Seasons 1, 2, and 4"; "No seasons".</summary>
    public static string SeasonList(IReadOnlyCollection<int> seasons)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        var sorted = seasons.Distinct().Order().Select(Number).ToList();
        return sorted.Count switch
        {
            0 => "No seasons",
            1 => $"Season {sorted[0]}",
            2 => $"Seasons {sorted[0]} and {sorted[1]}",
            _ => $"Seasons {string.Join(", ", sorted.Take(sorted.Count - 1))}, and {sorted[^1]}",
        };
    }

    /// <summary>"10 GB" for whole gigabytes, as the limits are chosen; another client's limit as its size, "1.5 GB".</summary>
    public static string LimitText(long maxBytes) =>
        maxBytes > 0 && maxBytes % DownloadPreferenceLimits.BytesPerGb == 0
            ? $"{Number((int)(maxBytes / DownloadPreferenceLimits.BytesPerGb))} GB"
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
        if (activity.Any(a => a.Phase == MonitorPhase.StorageLimit))
        {
            return new MonitorStatus.StorageLimit(monitor.MaxStorageBytes > 0 ? monitor.MaxStorageBytes : downloadsLimit ?? 0);
        }
        var ordered = activity.OrderBy(a => a.SeasonNumber ?? 0).ThenBy(a => a.EpisodeNumber ?? 0).ToList();
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
            MonitorStatus.Paused => "Paused",
            MonitorStatus.StorageLimit limit => $"Out of space · {LimitText(limit.MaxBytes)} limit",
            MonitorStatus.Downloading { EpisodeNumber: { } episode } downloading => $"Downloading Episode {Number(episode)} · {Percent(downloading.Fraction)}%",
            MonitorStatus.Downloading downloading => $"Downloading an episode · {Percent(downloading.Fraction)}%",
            MonitorStatus.Waiting waiting => WaitingLine(waiting.EpisodeNumber, waiting.Phase),
            MonitorStatus.Next { Episode: var next } => NextLine(next, now, zone),
            MonitorStatus.UpToDate => "Up to date",
            _ => "Monitoring",
        };
    }

    /// <summary>The download dialog's line for the next airing: "Episode 6 downloads Thursday", "Season 2 downloads tomorrow".</summary>
    public static string Headline(MonitorStatus status, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(status);
        switch (status)
        {
            case MonitorStatus.Paused:
                return "Monitoring is paused";
            case MonitorStatus.Next { Episode: var next }:
                string what = next.EpisodeNumber == 1 ? $"Season {Number(next.SeasonNumber)}" : $"Episode {Number(next.EpisodeNumber)}";
                return Day(next.AirDate, now, zone) switch
                {
                    RelativeDay.Today => $"{what} downloads today",
                    RelativeDay.Tomorrow => $"{what} downloads tomorrow",
                    var day => $"{what} downloads {DayText(next.AirDate, zone, day)}",
                };
            default:
                return StatusLine(status, now, zone);
        }
    }

    private static string NextLine(UpcomingEpisode next, DateTimeOffset now, TimeZoneInfo zone)
    {
        bool premiere = next.EpisodeNumber == 1;
        string season = Number(next.SeasonNumber);
        string episode = Number(next.EpisodeNumber);
        return Day(next.AirDate, now, zone) switch
        {
            RelativeDay.Today => premiere ? $"Season {season} starts today" : $"Episode {episode} today",
            RelativeDay.Tomorrow => premiere ? $"Season {season} starts tomorrow" : $"Episode {episode} tomorrow",
            var day => premiere ? $"Season {season} starts on {DayText(next.AirDate, zone, day)}" : $"Episode {episode} on {DayText(next.AirDate, zone, day)}",
        };
    }

    private static string WaitingLine(int? episode, MonitorPhase phase)
    {
        if (episode is not { } number)
        {
            return phase switch
            {
                MonitorPhase.WaitingForUnmetered => "An episode waits for an unmetered connection",
                MonitorPhase.Preparing => "Preparing an episode",
                _ => "An episode is next",
            };
        }
        string n = Number(number);
        return phase switch
        {
            MonitorPhase.WaitingForUnmetered => $"Episode {n} waits for an unmetered connection",
            MonitorPhase.Preparing => $"Preparing Episode {n}",
            _ => $"Episode {n} is next",
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

    // "Thursday" within the week, else "Nov 30".
    private static string DayText(DateTimeOffset at, TimeZoneInfo zone, RelativeDay day)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        return day == RelativeDay.ThisWeek ? local.ToString("dddd", English) : local.ToString("MMM d", English);
    }

    private static string Percent(double fraction) => ((int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
