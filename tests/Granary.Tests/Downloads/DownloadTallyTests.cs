using Granary.Core.Downloads;

namespace Granary.Tests.Downloads;

/// <summary>
/// What the shell shows of the downloads outside their page: how many are coming or waiting for the sidebar's count, and
/// whether one comes now for its pulse.
/// </summary>
public sealed class DownloadTallyTests
{
    private static DownloadItem Item(string id, DownloadState state, bool paused = false, bool gone = false, bool missing = false,
        bool metered = false, long expected = 1000, long done = 0) =>
        new(id, "ep_" + id, "Chapter Five", "sr_1", "Example Series", 1, 5, state, DownloadFailure.None, paused, gone, missing, metered,
            ExpectedBytes: expected, BytesDone: done, PreparationProgress: null, PosterPath: null, MediaFileId: null);

    [Fact]
    public void Pending_CountsWhatIsComingOrWaiting_NotWhatIsPausedFailedOrDone()
    {
        var tally = DownloadTally.Of(
        [
            Item("1", DownloadState.Preparing),
            Item("2", DownloadState.Queued),
            Item("3", DownloadState.Downloading),
            // Waiting for a connection that is not metered: it comes by itself once there is one.
            Item("4", DownloadState.Queued, metered: true),
            Item("5", DownloadState.Downloading, paused: true),
            Item("6", DownloadState.Failed),
            Item("7", DownloadState.Completed),
            // Nothing brings these any more: the server no longer has it, or its drive is not there.
            Item("8", DownloadState.Queued, gone: true),
            Item("9", DownloadState.Queued, missing: true),
        ]);

        Assert.Equal(4, tally.Pending);
    }

    [Fact]
    public void IsTransferring_OnlyWhileADownloadComes()
    {
        Assert.True(DownloadTally.Of([Item("1", DownloadState.Queued), Item("2", DownloadState.Downloading)]).IsTransferring);
        Assert.False(DownloadTally.Of([Item("1", DownloadState.Queued), Item("2", DownloadState.Preparing)]).IsTransferring);
        Assert.False(DownloadTally.Of([Item("1", DownloadState.Downloading, paused: true)]).IsTransferring);
        Assert.False(DownloadTally.Of([Item("1", DownloadState.Downloading, missing: true)]).IsTransferring);
        Assert.False(DownloadTally.Of([Item("1", DownloadState.Completed), Item("2", DownloadState.Failed)]).IsTransferring);
        Assert.Equal(DownloadTally.None, DownloadTally.Of([]));
    }
}
