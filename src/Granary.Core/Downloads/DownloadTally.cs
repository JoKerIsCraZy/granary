namespace Granary.Core.Downloads;

/// <summary>
/// What the shell shows of the active profile's downloads outside their page (docs/architecture.md, "Downloads in the shell"):
/// <see cref="Pending"/>, how many are coming or waiting, for the sidebar's count; <see cref="IsTransferring"/>, whether one comes
/// now, for its pulse; <see cref="Progress"/>, how far they are, for the taskbar button.
/// </summary>
public sealed record DownloadTally(int Pending, bool IsTransferring, DownloadProgress Progress)
{
    public static readonly DownloadTally None = new(0, false, DownloadProgress.None);

    /// <summary>
    /// The download is still to come by itself: preparing, queued or downloading, not paused by the viewer, its folder there and
    /// the server still has it. One that waits for a connection that is not metered counts: it comes once there is one.
    /// </summary>
    public static bool IsPending(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item is { State: DownloadState.Preparing or DownloadState.Queued or DownloadState.Downloading, IsPausedByViewer: false,
            FilesMissing: false, IsNoLongerAvailable: false };
    }

    public static DownloadTally Of(IReadOnlyCollection<DownloadItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new DownloadTally(items.Count(IsPending), items.Any(DownloadText.IsComing), ProgressOf(items));
    }

    // The downloads still to come, paused by the viewer or not: none, no bar. While any of them is not paused (and does not wait
    // for a connection that is not metered) the bar is a normal one over those, so a paused download does not hold it back;
    // once every one is, a paused bar over all of them. A size the server has not said yet adds nothing.
    private static DownloadProgress ProgressOf(IReadOnlyCollection<DownloadItem> items)
    {
        var toCome = items.Where(i => i is { State: DownloadState.Preparing or DownloadState.Queued or DownloadState.Downloading,
            FilesMissing: false, IsNoLongerAvailable: false }).ToList();
        if (toCome.Count == 0)
        {
            return DownloadProgress.None;
        }
        var moving = toCome.Where(i => !i.IsPausedByViewer && !i.WaitsForUnmetered).ToList();
        var shown = moving.Count > 0 ? moving : toCome;
        var sized = shown.Where(i => i.ExpectedBytes > 0).ToList();
        return new DownloadProgress(moving.Count > 0 ? DownloadProgressState.Normal : DownloadProgressState.Paused,
            sized.Sum(i => Math.Clamp(i.BytesDone, 0, i.ExpectedBytes)), sized.Sum(i => i.ExpectedBytes));
    }
}

/// <summary>How the taskbar button shows the downloads' progress: not at all, as a bar, or as a paused bar.</summary>
public enum DownloadProgressState
{
    None,
    Normal,
    Paused,
}

/// <summary>The downloads' overall progress for the taskbar button: <see cref="Done"/> of <see cref="Total"/> bytes.</summary>
public sealed record DownloadProgress(DownloadProgressState State, long Done, long Total)
{
    public static readonly DownloadProgress None = new(DownloadProgressState.None, 0, 0);

    /// <summary>How much is here, 0 to 1; 0 while no size is known.</summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}
