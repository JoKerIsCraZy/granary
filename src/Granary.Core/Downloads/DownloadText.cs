using System.Globalization;
using Granary.Core.Api.Downloads;

namespace Granary.Core.Downloads;

/// <summary>
/// The words of a download's status and of a quality preset. The web has no downloads page, so they are the apps':
/// "Downloading · 42%", "Downloaded", "Original", "10 Mbps · up to 1080p".
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
