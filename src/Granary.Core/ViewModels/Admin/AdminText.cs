using System.Globalization;
using Granary.Core.Api.Admin;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// How the admin pages word their numbers and sessions, as the web's dashboard does
/// (<c>components/admin/dashboard/format.ts</c>, <c>pages/adminActivityPresentation.ts</c>).
/// </summary>
public static class AdminText
{
    /// <summary>What a value the server did not give shows.</summary>
    public const string None = "—";

    private static readonly CultureInfo English = CultureInfo.InvariantCulture;
    private static readonly string[] DataUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Count(long value) => value.ToString("N0", English);

    /// <summary>"1 file", "12,480 files".</summary>
    public static string Files(int count) => count == 1 ? "1 file" : $"{Count(count)} files";

    public static string Plays(int plays) => plays == 1 ? "1 play" : $"{Count(plays)} plays";

    public static string Sessions(int sessions) => sessions == 1 ? "1 session" : $"{Count(sessions)} sessions";

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

    /// <summary>"Just now", "5m ago", "2h ago", "3d ago", each unit rounded down.</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        if (span.TotalMinutes < 1)
        {
            return "Just now";
        }
        return span.TotalHours < 1 ? $"{(long)span.TotalMinutes}m ago"
            : span.TotalDays < 1 ? $"{(long)span.TotalHours}h ago"
            : $"{(long)span.TotalDays}d ago";
    }

    /// <summary>The dashboard's "Updated …": "less than 1 minute ago", "1 minute ago", "5 minutes ago".</summary>
    public static string Updated(DateTimeOffset updatedAt, DateTimeOffset now)
    {
        long minutes = (long)Math.Floor(Math.Max(0, (now - updatedAt).TotalMinutes));
        return minutes switch
        {
            < 1 => "Updated less than 1 minute ago",
            1 => "Updated 1 minute ago",
            _ => $"Updated {Count(minutes)} minutes ago",
        };
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
            return session.EpisodeName is { Length: > 0 } name ? name : $"S{session.SeasonNumber}E{session.EpisodeNumber}";
        }
        return session.MediaTitle.Length > 0 ? session.MediaTitle : $"File #{session.MediaFileId}";
    }

    /// <summary>The line under the title: "S1 · E3 — Dark" for an episode, "Movie" or "Series" otherwise.</summary>
    public static string Subtitle(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (IsEpisode(session))
        {
            return $"S{session.SeasonNumber} · E{session.EpisodeNumber} — {session.SeriesName}";
        }
        return session.MediaType switch
        {
            "" => "",
            "movie" => "Movie",
            _ => "Series",
        };
    }

    /// <summary>The account that watches; "User #id" when the server sent no name.</summary>
    public static string Username(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Username.Length > 0 ? session.Username : $"User #{session.UserId}";
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
