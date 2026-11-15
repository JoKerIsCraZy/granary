namespace Granary.Core.Downloads;

/// <summary>
/// What the shell shows of the active profile's downloads outside their page (docs/architecture.md, "Downloads in the shell"):
/// <see cref="Pending"/>, how many are coming or waiting, for the sidebar's count; <see cref="IsTransferring"/>, whether one comes
/// now, for its pulse.
/// </summary>
public sealed record DownloadTally(int Pending, bool IsTransferring)
{
    public static readonly DownloadTally None = new(0, false);

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
        return new DownloadTally(items.Count(IsPending), items.Any(DownloadText.IsComing));
    }
}
