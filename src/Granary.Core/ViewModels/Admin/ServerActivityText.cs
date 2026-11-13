using System.Globalization;
using Granary.Core.Api.Admin;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The words and numbers of the activity indicator, as the web writes them (<c>components/ServerActivity.tsx</c> and
/// <c>pages/adminDownloadPreparationPresentation.ts</c>), in English.
/// </summary>
public static class ServerActivityText
{
    private const int MaxCount = 99;

    /// <summary>"3", "99+" past 99; a count that is only a lower bound always ends in "+".</summary>
    public static string Count(int count, bool isLowerBound = false) =>
        count > MaxCount ? $"{MaxCount}+" : count.ToString(CultureInfo.InvariantCulture) + (isLowerBound ? "+" : "");

    /// <summary>
    /// A task's progress, 0 to 100, as the web's <c>formatTaskProgress</c>: "42%", "12.5%", and never "100.0%" for a task
    /// that reports just under 100 while it still runs.
    /// </summary>
    public static string TaskProgress(double progress)
    {
        double clamped = ClampPercent(progress);
        if (clamped == Math.Floor(clamped))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{clamped:0}%");
        }
        double shown = Math.Round(clamped, 1, MidpointRounding.AwayFromZero) >= 100 ? 99.9 : clamped;
        return string.Create(CultureInfo.InvariantCulture, $"{shown:0.0}%");
    }

    /// <summary>0 to 100; what is no number counts as 0.</summary>
    public static double ClampPercent(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    /// <summary>"3 more scans queued" for the scans the panel does not list, "At least 475 more scans queued" when their number is a lower bound.</summary>
    public static string MoreScans(int count, bool isLowerBound)
    {
        string more = string.Create(CultureInfo.InvariantCulture, $"{count:N0} more {(count == 1 ? "scan" : "scans")} queued");
        return isLowerBound ? "At least " + more : more;
    }

    /// <summary>What a scan covers: "Full library", "Subtree" or "Single file"; a mode the web has no words for as the server names it.</summary>
    public static string ScanMode(string mode) => mode switch
    {
        "library" => "Full library",
        "subtree" => "Subtree",
        "file" => "Single file",
        _ => mode,
    };

    /// <summary>
    /// How far a scan is, as the web's <c>formatScanProgress</c>: "Matching files · 1,200 / 4,800 (25%)" once it counts
    /// files ("Processing files" when it does not say what it does), else what it says, else nothing.
    /// </summary>
    public static string ScanProgress(AdminScanResult? result)
    {
        if (result is null)
        {
            return "";
        }
        if (result.TotalFiles != 0 && result.FilesProcessed != 0)
        {
            double percent = Math.Clamp(Math.Round((double)result.FilesProcessed / result.TotalFiles * 100, MidpointRounding.AwayFromZero), 0, 100);
            string message = string.IsNullOrEmpty(result.Message) ? "Processing files" : result.Message;
            return string.Create(CultureInfo.InvariantCulture, $"{message} · {result.FilesProcessed:N0} / {result.TotalFiles:N0} ({percent:0}%)");
        }
        return result.Message ?? "";
    }

    /// <summary>A count as the web prints it in English: "1,234".</summary>
    public static string Number(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"2 more running · 4 queued · 1 waiting to retry", each part only when there is any; empty without.</summary>
    public static string WaitingPreparations(int moreRunning, int queued, int retrying) => string.Join(" · ", new[]
    {
        moreRunning > 0 ? $"{Number(moreRunning)} more running" : "",
        queued > 0 ? $"{Number(queued)} queued" : "",
        retrying > 0 ? $"{Number(retrying)} waiting to retry" : "",
    }.Where(part => part.Length > 0));

    /// <summary>
    /// A job's title on one line, as the web's <c>preparationCompactTitle</c>: "Dark S01E03" for an episode, else the
    /// title, else "File #48211".
    /// </summary>
    public static string PreparationTitle(AdminDownloadPreparation job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!string.IsNullOrEmpty(job.SeriesName) && job.SeasonNumber is { } season && job.EpisodeNumber is { } episode)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{job.SeriesName} S{season:00}E{episode:00}");
        }
        return job.MediaTitle.Length > 0 ? job.MediaTitle : $"File #{job.MediaFileId}";
    }

    /// <summary>"Remux", "Transcode", or "Transcode 1080p H.264" when the video is encoded anew (<c>formatPreparationKind</c>).</summary>
    public static string PreparationKind(AdminDownloadPreparation job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Format == "remux")
        {
            return "Remux";
        }
        string target = job.Output?.VideoCodec == "copy" ? ""
            : string.Join(" ", new[] { job.Output?.Resolution, AdminSessionText.Codec(job.Output?.VideoCodec) }.Where(part => !string.IsNullOrEmpty(part)));
        return target.Length > 0 ? $"Transcode {target}" : "Transcode";
    }

    /// <summary>Where a job runs: the node's or server's name, "Node 3" or "Node" for a nameless node, "Local server" for a nameless server; empty before a worker was chosen.</summary>
    public static string PreparationWorker(AdminDownloadPreparationWorker? worker)
    {
        if (worker is null)
        {
            return "";
        }
        if (!string.IsNullOrEmpty(worker.Name))
        {
            return worker.Name;
        }
        return worker.Kind != "node" ? "Local server" : string.IsNullOrEmpty(worker.NodeId) ? "Node" : $"Node {worker.NodeId}";
    }

    /// <summary>How far a job is, 0 to 100; null while its progress or length is not known (<c>preparationPercent</c>).</summary>
    public static double? PreparationPercent(AdminDownloadPreparationProgress? progress) =>
        progress is { DurationSeconds: > 0 } ? ClampPercent(progress.EncodedSeconds / progress.DurationSeconds * 100) : null;

    /// <summary>
    /// How long a job still takes at its speed, as the web's <c>formatRemaining</c>: "under 1 min left", "about 12 min
    /// left", "about 2 h left", "about 2 h 5 min left"; empty while that is not known.
    /// </summary>
    public static string PreparationRemaining(AdminDownloadPreparationProgress? progress)
    {
        if (progress is not { DurationSeconds: > 0, Speed: > 0 })
        {
            return "";
        }
        double seconds = Math.Max(0, progress.DurationSeconds - progress.EncodedSeconds) / progress.Speed;
        if (seconds < 60)
        {
            return "under 1 min left";
        }
        long minutes = (long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (minutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"about {minutes} min left");
        }
        long hours = minutes / 60;
        long rest = minutes % 60;
        return rest == 0
            ? string.Create(CultureInfo.InvariantCulture, $"about {hours} h left")
            : string.Create(CultureInfo.InvariantCulture, $"about {hours} h {rest} min left");
    }
}
