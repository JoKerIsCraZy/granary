using System.Globalization;
using Granary.Core.Api.Requests;

namespace Granary.Core.ViewModels;

/// <summary>How a request's state badge is drawn, as the web's badge variants.</summary>
public enum RequestBadgeLook
{
    /// <summary>Pending: an outline.</summary>
    Outline,

    /// <summary>Declined and cancelled: an outline in the muted colour.</summary>
    Muted,

    /// <summary>Approved, processing, partially available: the secondary fill.</summary>
    Secondary,

    /// <summary>Available: the red fill.</summary>
    Primary,

    /// <summary>Failed.</summary>
    Destructive,
}

/// <summary>A request worded as the web's <c>lib/mediaRequests.ts</c> and <c>lib/requestDownload.ts</c> word it.</summary>
internal static class RequestText
{
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    /// <summary>
    /// The state the request pages show: the server's, or for an older server the status and outcome collapsed the same
    /// way (queued and downloading read as processing, completed as available, a closed outcome over its status).
    /// </summary>
    public static string? DisplayState(string? status, string? outcome, string? state)
    {
        if (!string.IsNullOrEmpty(state))
        {
            return state;
        }
        if (outcome is RequestStates.Declined or RequestStates.Cancelled or RequestStates.Failed)
        {
            return outcome;
        }
        return status switch
        {
            "pending" or "approved" => status,
            "queued" or "downloading" => RequestStates.Processing,
            "completed" => RequestStates.Available,
            _ => null,
        };
    }

    public static string StateLabel(string state) => state switch
    {
        RequestStates.Pending => "Pending",
        RequestStates.Approved => "Approved",
        RequestStates.Processing => "Processing",
        RequestStates.PartiallyAvailable => "Partially available",
        RequestStates.Available => "Available",
        RequestStates.Declined => "Declined",
        RequestStates.Cancelled => "Cancelled",
        RequestStates.Failed => "Failed",
        _ => "Active",
    };

    public static RequestBadgeLook Look(string state) => state switch
    {
        RequestStates.Pending => RequestBadgeLook.Outline,
        RequestStates.Approved or RequestStates.Processing or RequestStates.PartiallyAvailable => RequestBadgeLook.Secondary,
        RequestStates.Available => RequestBadgeLook.Primary,
        RequestStates.Failed => RequestBadgeLook.Destructive,
        _ => RequestBadgeLook.Muted,
    };

    /// <summary>Why a title without a request cannot be requested.</summary>
    public static string ReasonLabel(string? reason) => reason switch
    {
        "already_requested" => "Already requested",
        "already_available" => "Available",
        "requests_disabled" => "Requests disabled",
        "blocked" => "Blocked",
        "quota_exceeded" => "Request limit reached",
        _ => "Unavailable",
    };

    public static string MediaTypeLabel(string mediaType) => mediaType == "series" ? "Series" : "Movie";

    /// <summary>
    /// The owner can withdraw a request until something was sent for it: while pending, or approved with no target yet.
    /// </summary>
    public static bool CanCancel(MediaRequest request) =>
        request.Outcome == "active" && (request.Status == "pending" || (request.Status == "approved" && request.Targets.Count == 0));

    /// <summary>"Season 2", "Seasons 1–3, 5".</summary>
    public static string SeasonList(IReadOnlyList<int> seasons)
    {
        var sorted = seasons.Distinct().Order().ToList();
        var runs = new List<string>();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
            {
                j++;
            }
            runs.Add(j > i ? $"{sorted[i]}–{sorted[j]}" : sorted[i].ToString(English));
            i = j + 1;
        }
        return $"{(sorted.Count == 1 ? "Season" : "Seasons")} {string.Join(", ", runs)}";
    }

    /// <summary>"14 of 20 episodes in the library", counting aired episodes; "" when none has aired.</summary>
    public static string SeasonProgress(IReadOnlyList<RequestSeasonProgress> progress)
    {
        int aired = progress.Sum(p => p.EpisodesAired);
        int have = progress.Sum(p => Math.Min(p.EpisodesAvailable, p.EpisodesAired));
        return aired == 0 ? "" : $"{have} of {aired} {(aired == 1 ? "episode" : "episodes")} in the library";
    }

    /// <summary>How far the downloads are, for a bar; null while the size is unknown or the phase is not a download's.</summary>
    public static double? DownloadPercent(RequestDownload download) =>
        download.Percent is { } percent && download.Phase is "queued" or "downloading" or "paused" or "stalled" or "importing" or "import_blocked"
            ? Math.Clamp(percent, 0, 100)
            : null;

    /// <summary>"Downloading · 43% · about 12 min left", or the phase alone.</summary>
    public static string Download(RequestDownload download, DateTimeOffset now)
    {
        switch (download.Phase)
        {
            case "queued":
                return "Waiting to download";
            case "paused":
                return "Download paused";
            case "stalled":
                return "Download stalled";
            case "importing":
                return "Importing";
            case "import_blocked":
                return "Waiting for import";
            case "downloading":
                break;
            default:
                return "Downloading";
        }
        var parts = new List<string> { "Downloading" };
        if (DownloadPercent(download) is { } percent)
        {
            // The figure as the server sent it, as the web prints it.
            parts.Add($"{percent.ToString("0.##", English)}%");
        }
        // An estimate that passed, or one the server last heard of more than ten minutes ago, is left out.
        if (download.EstimatedCompletionAt is { } eta && eta > now && now - download.UpdatedAt <= TimeSpan.FromMinutes(10))
        {
            parts.Add($"about {HoursMinutes(eta - now)} left");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>"Oct 5, 2026".</summary>
    public static string Date(DateTimeOffset value, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(value, zone).ToString("MMM d, yyyy", English);

    /// <summary>"Updated 3h ago" wording for a change after the request was made; null when it changed within a minute of it.</summary>
    public static string? LastChange(MediaRequest request, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (request.UpdatedAt - request.CreatedAt < TimeSpan.FromMinutes(1))
        {
            return null;
        }
        var ago = now - request.UpdatedAt;
        int minutes = (int)Math.Round(ago.TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes < 1)
        {
            return "just now";
        }
        if (minutes < 60)
        {
            return $"{minutes}m ago";
        }
        int hours = (int)Math.Round(ago.TotalHours, MidpointRounding.AwayFromZero);
        if (hours < 24)
        {
            return $"{hours}h ago";
        }
        int days = (int)Math.Round(ago.TotalDays, MidpointRounding.AwayFromZero);
        return days < 7 ? $"{days}d ago" : Date(request.UpdatedAt, zone);
    }

    private static string HoursMinutes(TimeSpan span)
    {
        int total = Math.Max(1, (int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero));
        int hours = total / 60;
        int minutes = total % 60;
        return hours == 0 ? $"{minutes} min" : minutes == 0 ? $"{hours} hr" : $"{hours} hr {minutes} min";
    }
}
