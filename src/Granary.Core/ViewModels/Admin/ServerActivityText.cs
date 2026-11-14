using System.Globalization;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// The words and numbers of the activity indicator, as the web writes them (<c>components/ServerActivity.tsx</c> and
/// <c>pages/adminDownloadPreparationPresentation.ts</c>), in the language on screen.
/// </summary>
public static class ServerActivityText
{
    private const int MaxCount = 99;

    /// <summary>"3", "99+" past 99; a count that is only a lower bound always ends in "+".</summary>
    public static string Count(int count, bool isLowerBound = false) =>
        count > MaxCount ? $"{MaxCount}+" : count.ToString(CultureInfo.InvariantCulture) + (isLowerBound ? "+" : "");

    /// <summary>
    /// A task's progress, 0 to 100, as the web's <c>formatTaskProgress</c>: "42%", "12.5%", "42.0%", and never "100.0%" for a task
    /// that reports just under 100 while it still runs.
    /// </summary>
    public static string TaskProgress(double progress)
    {
        double clamped = ClampPercent(progress);
        if (clamped == Math.Floor(clamped))
        {
            return AppText.Get("web.admin.tasks.lib.taskProgress.percent", ("percent", clamped));
        }
        double shown = Math.Round(clamped, 1, MidpointRounding.AwayFromZero);
        // One decimal always, as the web's formatDecimal(shown, 1): 41.97 is "42.0%", not "42%", in the language's own way.
        string written = (shown >= 100 ? 99.9 : shown).ToString("0.0", AppText.Language.Culture);
        return AppText.Get("web.admin.tasks.lib.taskProgress.percent", ("percent", written));
    }

    /// <summary>0 to 100; what is no number counts as 0.</summary>
    public static double ClampPercent(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    /// <summary>"3 more scans queued" for the scans the panel does not list, "At least 475 more scans queued" when their number is a lower bound.</summary>
    public static string MoreScans(int count, bool isLowerBound) => AppText.Get(
        isLowerBound ? "web.admin.common.serverActivity.moreScansAtLeast" : "web.admin.common.serverActivity.moreScans",
        ("count", count), ("count_text", AppText.Number(count)));

    /// <summary>What a scan covers: "Full library", "Subtree" or "Single file"; a mode the web has no words for as the server names it.</summary>
    public static string ScanMode(string mode) => mode switch
    {
        "library" => AppText.Get("web.admin.common.serverActivity.scanMode.library"),
        "subtree" => AppText.Get("web.admin.common.serverActivity.scanMode.subtree"),
        "file" => AppText.Get("web.admin.common.serverActivity.scanMode.file"),
        _ => mode,
    };

    /// <summary>
    /// How far a scan is, as the web's <c>formatScanProgress</c>: "Matching files · 1,200 / 4,800 (25%)" once it counts
    /// files ("Processing files" when it does not say what it does), else what it says, else nothing. What it does is the
    /// server's English, as on the web.
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
            string message = string.IsNullOrEmpty(result.Message) ? AppText.Get("web.admin.common.serverActivity.processingFiles") : result.Message;
            return AppText.Get("web.admin.common.serverActivity.scanProgress", ("message", message),
                ("processed", AppText.Number(result.FilesProcessed)), ("total", AppText.Number(result.TotalFiles)), ("percent", percent));
        }
        return result.Message ?? "";
    }

    /// <summary>"2 more running · 4 queued · 1 waiting to retry", each part only when there is any; empty without.</summary>
    public static string WaitingPreparations(int moreRunning, int queued, int retrying) => string.Join(" · ", new[]
    {
        moreRunning > 0 ? AppText.Get("web.admin.common.serverActivity.moreRunning", ("count", moreRunning), ("count_text", moreRunning)) : "",
        queued > 0 ? AppText.Get("web.admin.common.serverActivity.queued", ("count", queued), ("count_text", AppText.Number(queued))) : "",
        retrying > 0 ? AppText.Get("web.admin.common.serverActivity.retrying", ("count", retrying), ("count_text", AppText.Number(retrying))) : "",
    }.Where(part => part.Length > 0));

    /// <summary>"2 paused"; empty without any.</summary>
    public static string PausedPreparations(int paused) =>
        paused > 0 ? AppText.Get("web.admin.common.serverActivity.paused", ("count", paused), ("count_text", AppText.Number(paused))) : "";

    /// <summary>"1 failed in the last 24 hours"; empty without any.</summary>
    public static string FailedPreparations(int failed) =>
        failed > 0 ? AppText.Get("web.admin.common.serverActivity.failedRecent", ("count", failed), ("count_text", AppText.Number(failed))) : "";

    /// <summary>
    /// A job's title on one line, as the web's <c>preparationCompactTitle</c>: "Dark S01E03" for an episode, else the
    /// title, else "File #48211".
    /// </summary>
    public static string PreparationTitle(AdminDownloadPreparation job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!string.IsNullOrEmpty(job.SeriesName) && job.SeasonNumber is { } season && job.EpisodeNumber is { } episode)
        {
            string code = AppText.Get("web.admin.downloads.preparation.episodeCode",
                ("season", season.ToString("00", CultureInfo.InvariantCulture)), ("episode", episode.ToString("00", CultureInfo.InvariantCulture)));
            return AppText.Get("web.admin.downloads.preparation.compactEpisodeTitle", ("series", job.SeriesName), ("code", code));
        }
        return job.MediaTitle.Length > 0 ? job.MediaTitle : AppText.Get("web.admin.downloads.preparation.fileFallback", ("id", job.MediaFileId));
    }

    /// <summary>"Remux", "Transcode", or "Transcode 1080p H.264" when the video is encoded anew (<c>formatPreparationKind</c>).</summary>
    public static string PreparationKind(AdminDownloadPreparation job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Format == "remux")
        {
            return AppText.Get("web.admin.downloads.preparation.kind.remux");
        }
        string target = job.Output?.VideoCodec == "copy" ? ""
            : string.Join(" ", new[] { job.Output?.Resolution, AdminSessionText.Codec(job.Output?.VideoCodec) }.Where(part => !string.IsNullOrEmpty(part)));
        return target.Length > 0
            ? AppText.Get("web.admin.downloads.preparation.kind.transcodeTarget", ("target", target))
            : AppText.Get("web.admin.downloads.preparation.kind.transcode");
    }

    /// <summary>"Encoding", or "Remuxing" for a remux, which copies its streams: what a job does while its progress is unknown.</summary>
    public static string PreparationRunning(AdminDownloadPreparation job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return AppText.Get(job.Format == "remux" ? "web.admin.downloads.preparation.running.remuxing" : "web.admin.downloads.preparation.running.encoding");
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
        return worker.Kind != "node" ? AppText.Get("web.admin.downloads.preparation.worker.localServer")
            : string.IsNullOrEmpty(worker.NodeId) ? AppText.Get("web.admin.downloads.preparation.worker.node")
            : AppText.Get("web.admin.downloads.preparation.worker.nodeWithId", ("id", worker.NodeId));
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
            return AppText.Get("web.admin.downloads.preparation.remaining.underOneMinute");
        }
        long minutes = (long)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (minutes < 60)
        {
            return AppText.Get("web.admin.downloads.preparation.remaining.minutes", ("minutes", minutes));
        }
        long hours = minutes / 60;
        long rest = minutes % 60;
        return rest == 0
            ? AppText.Get("web.admin.downloads.preparation.remaining.hours", ("hours", hours), ("hours_text", hours))
            : AppText.Get("web.admin.downloads.preparation.remaining.hoursMinutes", ("hours", hours), ("hours_text", hours), ("minutes", rest));
    }

    /// <summary>
    /// A running task's name in the language on screen, as the web's <c>taskText</c>: Silo's own tasks have the web's
    /// message by the task's key (<c>web.admin.tasks.task.&lt;key&gt;.name</c>), any other, such as a plugin's, shows the
    /// server's English as sent.
    /// </summary>
    public static string TaskName(string key, string english)
    {
        if (key.Length == 0 || !key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
        {
            return english;
        }
        return AppText.ServerText($"web.admin.tasks.task.{key.Replace('-', '_')}.name", null, english);
    }
}
