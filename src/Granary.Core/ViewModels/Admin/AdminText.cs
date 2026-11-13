using System.Globalization;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// How the admin pages word their numbers and sessions, as the web's dashboard does
/// (<c>components/admin/dashboard/format.ts</c>, <c>pages/adminActivityPresentation.ts</c>): its words in the language on
/// screen, its numbers and units in English until numbers are converted, as the web's own units are.
/// </summary>
public static class AdminText
{
    /// <summary>What a value the server did not give shows.</summary>
    public const string None = "—";

    private static readonly CultureInfo English = CultureInfo.InvariantCulture;
    private static readonly string[] DataUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    // A stored library type in the spellings the server accepts, as the web's libraryTypes.ts reads them.
    private static readonly Dictionary<string, string> LibraryTypeAliases = new(StringComparer.Ordinal)
    {
        ["movie"] = "movies", ["tv"] = "series", ["show"] = "series", ["tvshows"] = "series", ["audiobook"] = "audiobooks",
        ["ebook"] = "ebooks", ["podcast"] = "podcasts",
    };

    public static string Count(long value) => value.ToString("N0", English);

    /// <summary>"1 file", "12,480 files".</summary>
    public static string Files(int count) =>
        AppText.Get("web.admin.dashboard.format.fileCount", ("count", count), ("count_text", AppText.Number(count)));

    /// <summary>A top title's plays: "1 play", "3 plays".</summary>
    public static string Plays(int plays) =>
        AppText.Get("web.admin.dashboard.widgets.topTitles.playCount", ("count", plays), ("count_text", AppText.Number(plays)));

    /// <summary>A most active profile's plays, in that list's words.</summary>
    public static string ProfilePlays(int plays) =>
        AppText.Get("web.admin.dashboard.widgets.topProfiles.playCount", ("count", plays), ("count_text", AppText.Number(plays)));

    /// <summary>The Active Streams tile's line: "1 session", "2 sessions".</summary>
    public static string Sessions(int sessions) =>
        AppText.Get("web.admin.dashboard.widgets.activeStreams.sessions", ("count", sessions), ("count_text", sessions));

    /// <summary>The playback chart's sessions: "12 sessions", the number grouped.</summary>
    public static string StartedSessions(int sessions) =>
        AppText.Get("web.admin.dashboard.widgets.playbackActivity.sessionCount", ("count", sessions), ("count_text", AppText.Number(sessions)));

    /// <summary>A card's title over the last 24 hours, as the web's range titles: "Egress · last 24 h".</summary>
    public static string LastDay(string title) => AppText.Get("web.admin.dashboard.range.title.day", ("title", title));

    /// <summary>A card's title over the last 7 days: "Top titles · last 7 d".</summary>
    public static string LastWeek(string title) => AppText.Get("web.admin.dashboard.range.title.week", ("title", title));

    /// <summary>"the last 24 hours", for a sentence about the day's window.</summary>
    public static string LastDayPhrase => AppText.Get("web.admin.dashboard.range.phrase.day");

    /// <summary>"the last 7 days", for a sentence about the week's window.</summary>
    public static string LastWeekPhrase => AppText.Get("web.admin.dashboard.range.phrase.week");

    /// <summary>A library's line, as the web's Libraries card: "Movies · 2 paths", a kind the web does not know as stored.</summary>
    public static string LibraryDetail(string type, int paths) =>
        AppText.Get("web.admin.dashboard.widgets.libraries.typeAndPaths", ("count", paths), ("count_text", paths), ("type", LibraryType(type)));

    /// <summary>A library's kind in the web's words ("Movies"), as stored when the web does not know it.</summary>
    public static string LibraryType(string type)
    {
        string kind = type?.Trim().ToLowerInvariant() ?? "";
        return LibraryTypeAliases.GetValueOrDefault(kind, kind) switch
        {
            "movies" => AppText.Get("web.common.libraryType.movies"),
            "series" => AppText.Get("web.common.libraryType.series"),
            "mixed" => AppText.Get("web.common.libraryType.mixed"),
            "audiobooks" => AppText.Get("web.common.libraryType.audiobooks"),
            "ebooks" => AppText.Get("web.common.libraryType.ebooks"),
            "manga" => AppText.Get("web.common.libraryType.manga"),
            "podcasts" => AppText.Get("web.common.libraryType.podcasts"),
            _ => type ?? "",
        };
    }

    /// <summary>An administrator's role badge: "owner" for the server's Owner, else "admin", as the web's accountRoleLabel.</summary>
    public static string Role(string role, bool isOwner) =>
        AppText.Get("web.admin.common.accounts.role", ("role", isOwner ? "owner" : role));

    /// <summary>"0.7 Mbps", "133 Mbps": one place under 10 Mbps, so a trickle does not read as idle.</summary>
    public static string Mbps(double mbps) => double.IsFinite(mbps) ? $"{MbpsValue(mbps)} Mbps" : None;

    /// <summary>The number of <see cref="Mbps"/> alone, for a chart's axis: "0.7", "133".</summary>
    public static string MbpsValue(double mbps)
    {
        if (!double.IsFinite(mbps))
        {
            return None;
        }
        return mbps is > 0 and < 10 ? Round(mbps, 1).ToString("#,0.0", English) : Round(mbps, 0).ToString("#,0", English);
    }

    /// <summary>
    /// Data moved over a window, in decimal units: "512 B", "48.3 GB", "1.2 TB". One place under 100, the unit picked on
    /// the rounded value, so it never reads "1,000 KB".
    /// </summary>
    public static string DataTotal(long bytes)
    {
        if (bytes < 0)
        {
            return None;
        }
        double value = bytes;
        int unit = 0;
        while (true)
        {
            double rounded = Round(value, unit > 0 && value < 100 ? 1 : 0);
            if (rounded >= 1000 && unit < DataUnits.Length - 1)
            {
                value /= 1000;
                unit++;
                continue;
            }
            string format = unit > 0 && rounded < 100 ? "#,0.0" : "#,0";
            return $"{rounded.ToString(format, English)} {DataUnits[unit]}";
        }
    }

    /// <summary>The dashboard's Storage tile, in binary units as the web words it: "101.8 TB", or "512 GB" under a terabyte.</summary>
    public static string Storage(long bytes)
    {
        double gigabytes = Math.Max(0, bytes) / (1024d * 1024 * 1024);
        double terabytes = gigabytes / 1024;
        return terabytes >= 1 ? $"{Round(terabytes, 1).ToString("0.0", English)} TB" : $"{Round(gigabytes, 0).ToString("0", English)} GB";
    }

    /// <summary>Watch time: "52m" under an hour (at least "1m"), "3.1h" from an hour on, "0m" for none.</summary>
    public static string WatchTime(long seconds)
    {
        if (seconds <= 0)
        {
            return "0m";
        }
        if (seconds < 3600)
        {
            return $"{Math.Max(1, (long)Round(seconds / 60d, 0))}m";
        }
        return $"{Round(seconds / 3600d, 1).ToString("#,0.#", English)}h";
    }

    /// <summary>"45s", "12m 3s", "3h 5m" under a day; "3d 4h" from a day on.</summary>
    public static string Uptime(DateTimeOffset startedAt, DateTimeOffset now)
    {
        long seconds = Math.Max(0, (long)Math.Floor((now - startedAt).TotalSeconds));
        if (seconds < 86_400)
        {
            return Duration(seconds);
        }
        long days = seconds / 86_400;
        long hours = seconds % 86_400 / 3600;
        return hours == 0 ? $"{days}d" : $"{days}d {hours}h";
    }

    /// <summary>"0.06 ms", "12 ms": round trips under 10 ms keep two places.</summary>
    public static string Latency(double ms)
    {
        if (!double.IsFinite(ms))
        {
            return None;
        }
        return ms < 10 ? $"{Round(ms, 2).ToString("#,0.##", English)} ms" : $"{Round(ms, 0).ToString("#,0", English)} ms";
    }

    /// <summary>
    /// "Just now", "5m ago", "2h ago", "3d ago", each unit rounded down, as the web's <c>formatRelativeTime</c>. Each card
    /// has its own "Just now" (<paramref name="justNow"/>); Now Playing's without one.
    /// </summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now, string? justNow = null)
    {
        var span = now - at;
        if (span.TotalMinutes < 1)
        {
            return justNow ?? AppText.Get("web.admin.dashboard.widgets.nowPlaying.justNow");
        }
        if (span.TotalHours < 1)
        {
            long minutes = (long)span.TotalMinutes;
            return AppText.Get("web.common.date.minutesAgo", ("count", minutes), ("count_text", minutes));
        }
        if (span.TotalDays < 1)
        {
            long hours = (long)span.TotalHours;
            return AppText.Get("web.common.date.hoursAgo", ("count", hours), ("count_text", hours));
        }
        long days = (long)span.TotalDays;
        return AppText.Get("web.common.date.daysAgo", ("count", days), ("count_text", days));
    }

    /// <summary>The dashboard's "Updated …": "less than 1 minute ago", "1 minute ago", "5 minutes ago".</summary>
    public static string Updated(DateTimeOffset updatedAt, DateTimeOffset now)
    {
        long minutes = (long)Math.Floor(Math.Max(0, (now - updatedAt).TotalMinutes));
        return minutes < 1
            ? AppText.Get("web.admin.dashboard.page.updatedJustNow")
            : AppText.Get("web.admin.dashboard.page.updatedMinutesAgo", ("minutes", minutes), ("minutes_text", AppText.Number(minutes)));
    }

    /// <summary>
    /// What a session's card calls it: an episode's name ("S1E3" without one), else the media's title ("File #id"
    /// without one).
    /// </summary>
    public static string Title(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (IsEpisode(session))
        {
            return session.EpisodeName is { Length: > 0 } name
                ? name
                : AppText.Get("web.admin.dashboard.widgets.nowPlaying.episodeFallback",
                    ("season", Plain(session.SeasonNumber)), ("episode", Plain(session.EpisodeNumber)));
        }
        return session.MediaTitle.Length > 0
            ? session.MediaTitle
            : AppText.Get("web.admin.dashboard.widgets.nowPlaying.fileFallback", ("id", session.MediaFileId));
    }

    /// <summary>The line under the title: "S1 · E3 — Dark" for an episode, "Movie" or "Series" otherwise.</summary>
    public static string Subtitle(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (IsEpisode(session))
        {
            return AppText.Get("web.admin.dashboard.widgets.nowPlaying.episodeLineWithSeries",
                ("season", Plain(session.SeasonNumber)), ("episode", Plain(session.EpisodeNumber)), ("series", session.SeriesName));
        }
        return session.MediaType switch
        {
            "" => "",
            "movie" => AppText.Get("web.admin.dashboard.widgets.nowPlaying.movie"),
            _ => AppText.Get("web.admin.dashboard.widgets.nowPlaying.series"),
        };
    }

    /// <summary>The account that watches; "User #id" when the server sent no name.</summary>
    public static string Username(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Username.Length > 0
            ? session.Username
            : AppText.Get("web.admin.dashboard.widgets.nowPlaying.userFallback", ("id", session.UserId));
    }

    /// <summary>The profile that watches, by name or else id; empty without either.</summary>
    public static string Profile(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.ProfileName is { Length: > 0 } name ? name : session.ProfileId;
    }

    /// <summary>The client: its label, else its name and version.</summary>
    public static string Client(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.ClientLabel?.Trim() is { Length: > 0 } label)
        {
            return label;
        }
        string name = session.ClientName?.Trim() ?? "";
        string version = session.ClientVersion?.Trim() ?? "";
        return name.Length > 0 && version.Length > 0 ? $"{name} {version}" : name;
    }

    /// <summary>The first letter of a name, for an avatar.</summary>
    public static string Initial(string name) =>
        string.IsNullOrEmpty(name) ? "" : char.ToUpperInvariant(name[0]).ToString();

    private static bool IsEpisode(AdminPlaybackSession session) =>
        !string.IsNullOrEmpty(session.SeriesName) && session.SeasonNumber is not null && session.EpisodeNumber is not null;

    // A season's or episode's number as the web passes it into a message, a string as written: "3".
    private static string Plain(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Duration(long seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds}s";
        }
        if (seconds < 3600)
        {
            long rest = seconds % 60;
            return rest == 0 ? $"{seconds / 60}m" : $"{seconds / 60}m {rest}s";
        }
        long minutes = seconds % 3600 / 60;
        return minutes == 0 ? $"{seconds / 3600}h" : $"{seconds / 3600}h {minutes}m";
    }

    // As JavaScript's toFixed and toLocaleString round: halves away from zero.
    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
