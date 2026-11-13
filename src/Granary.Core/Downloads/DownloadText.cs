using System.Globalization;
using Granary.Core.Api.Downloads;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.Downloads;

/// <summary>
/// The words of a download's status and of a quality preset. The web has no downloads page, so they are the apps':
/// "Downloading · 42%", "Downloaded", "12.3 MB/s", "4 min left", "Original", "10 Mbps · up to 1080p".
/// </summary>
public static class DownloadText
{
    public const string MissingFile = "Missing file";
    public const string NoLongerAvailable = "No longer available";
    public const string Paused = "Paused";
    public const string Preparing = "Preparing…";
    public const string Queued = "Queued";
    public const string PausedMetered = "Paused · metered connection";
    public const string Downloaded = "Downloaded";
    public const string Failed = "Failed";
    public const string FailedNoSpace = "Failed · not enough space";
    public const string Original = "Original";

    /// <summary>
    /// The status a row and the title page's menu show. Missing files come first (a folder that is gone, or a finished
    /// download whose media file was deleted: the Android app's "Missing file"), then a download the server no longer has,
    /// then the viewer's pause, then where it stands.
    /// </summary>
    public static string Status(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item switch
        {
            { FilesMissing: true } => MissingFile,
            { IsNoLongerAvailable: true } => NoLongerAvailable,
            { IsPausedByViewer: true } => Paused,
            { State: DownloadState.Preparing, PreparationProgress: { } progress } => $"{Preparing} {Percent(progress)}%",
            { State: DownloadState.Preparing } => Preparing,
            { State: DownloadState.Queued, WaitsForUnmetered: true } => PausedMetered,
            { State: DownloadState.Queued } => Queued,
            { State: DownloadState.Downloading } => $"Downloading · {Percent(item.Progress)}%",
            { State: DownloadState.Completed } => Downloaded,
            { Failure: DownloadFailure.NotEnoughSpace } => FailedNoSpace,
            _ => Failed,
        };
    }

    /// <summary>
    /// As <see cref="Status(DownloadItem)"/>, and while the file comes, the speed and the time left once they are known, as the
    /// Apple app's row says them: "Downloading · 42% · 12.3 MB/s · 4 min left". No speed under a byte a second.
    /// </summary>
    public static string Status(DownloadItem item, double? bytesPerSecond)
    {
        string status = Status(item);
        if (item.State != DownloadState.Downloading || item.FilesMissing || item.IsNoLongerAvailable || item.IsPausedByViewer
            || bytesPerSecond is not >= 1 || !double.IsFinite(bytesPerSecond.Value))
        {
            return status;
        }
        double rate = bytesPerSecond.Value;
        string withSpeed = $"{status} · {Speed(rate)}";
        long left = item.ExpectedBytes - item.BytesDone;
        return item.ExpectedBytes > 0 && left > 0 ? $"{withSpeed} · {TimeLeft(TimeSpan.FromSeconds(left / rate))}" : withSpeed;
    }

    /// <summary>"12.3 MB/s": the size a second, in the sizes the page says everywhere.</summary>
    public static string Speed(double bytesPerSecond) => PrePlayText.FileSize((long)Math.Max(1, bytesPerSecond)) + "/s";

    /// <summary>
    /// "4 min left", in whole minutes, rounded, as the Apple app says a download's time left; "under 1 min left" below half a
    /// minute; "1 hr 5 min left" from an hour, and "2 hr left" on the hour, as its preparation's time left says it.
    /// </summary>
    public static string TimeLeft(TimeSpan left)
    {
        long minutes = (long)Math.Round(Math.Max(0, left.TotalMinutes), MidpointRounding.AwayFromZero);
        if (minutes < 1)
        {
            return "under 1 min left";
        }
        if (minutes < 60)
        {
            return $"{minutes.ToString(CultureInfo.InvariantCulture)} min left";
        }
        string hours = (minutes / 60).ToString(CultureInfo.InvariantCulture);
        long rest = minutes % 60;
        return rest == 0 ? $"{hours} hr left" : $"{hours} hr {rest.ToString(CultureInfo.InvariantCulture)} min left";
    }

    /// <summary>"Original", or the preset's bitrate and, where the server says, its tallest output: "10 Mbps · up to 1080p".</summary>
    public static string QualityLabel(DownloadQualityOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (option.Preset == DownloadQualities.Original)
        {
            return Original;
        }
        string? mbps = option.BitrateKbps is { } kbps
            ? (kbps / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)
            : option.Preset.EndsWith("mbps", StringComparison.Ordinal) && int.TryParse(option.Preset[..^4], NumberStyles.None, CultureInfo.InvariantCulture, out int named)
                ? named.ToString(CultureInfo.InvariantCulture)
                : null;
        if (mbps is null)
        {
            return option.Preset;
        }
        return option.MaxHeight is { } height ? $"{mbps} Mbps · up to {height.ToString(CultureInfo.InvariantCulture)}p" : $"{mbps} Mbps";
    }

    // Whole percent, never rounded up: 99.9% is not shown as done.
    private static string Percent(double progress) =>
        ((int)Math.Floor(Math.Clamp(progress, 0, 1) * 100 + 1e-9)).ToString(CultureInfo.InvariantCulture);
}
