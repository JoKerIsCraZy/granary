using Granary.Core.Downloads;

namespace Granary.Tests.Downloads;

/// <summary>
/// What the shell shows of the downloads outside their page: how many are coming or waiting for the sidebar's count,
/// whether one comes now for its pulse, and how far they are for the taskbar button.
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

    [Fact]
    public void TheTaskbar_ShowsNothing_WhileNothingIsToCome()
    {
        var tally = DownloadTally.Of(
        [
            Item("1", DownloadState.Completed, done: 1000),
            Item("2", DownloadState.Failed, done: 300),
            Item("3", DownloadState.Queued, gone: true),
            Item("4", DownloadState.Downloading, missing: true, done: 200),
        ]);

        Assert.Equal(DownloadProgress.None, tally.Progress);
        Assert.Equal(DownloadProgress.None, DownloadTally.Of([]).Progress);
    }

    [Fact]
    public void TheTaskbar_IsNormal_WhileAnyIsToCome_OverTheBytesOfThoseNotPaused()
    {
        var tally = DownloadTally.Of(
        [
            Item("1", DownloadState.Downloading, expected: 1000, done: 250),
            // Waiting for its turn or for the server: still to come, its bytes count.
            Item("2", DownloadState.Queued, expected: 3000),
            // A preparing copy whose size the server has not said yet adds nothing it could not know.
            Item("3", DownloadState.Preparing, expected: 0),
            // Paused by the viewer while others come: it does not hold the bar back.
            Item("4", DownloadState.Downloading, paused: true, expected: 1000, done: 900),
            Item("5", DownloadState.Completed, expected: 2000, done: 2000),
        ]);

        Assert.Equal(new DownloadProgress(DownloadProgressState.Normal, 250, 4000), tally.Progress);
        Assert.Equal(0.0625, tally.Progress.Fraction);
    }

    [Fact]
    public void TheTaskbar_IsPaused_OnceEveryDownloadToComeIsPausedOrWaitsForAConnection_OverAllOfThem()
    {
        var paused = DownloadTally.Of(
        [
            Item("1", DownloadState.Downloading, paused: true, expected: 1000, done: 500),
            Item("2", DownloadState.Queued, paused: true, expected: 1000),
            Item("3", DownloadState.Completed, expected: 2000, done: 2000),
        ]);
        var metered = DownloadTally.Of(
        [
            Item("1", DownloadState.Queued, metered: true, expected: 1000, done: 400),
            Item("2", DownloadState.Queued, paused: true, metered: true, expected: 1000),
        ]);

        Assert.Equal(new DownloadProgress(DownloadProgressState.Paused, 500, 2000), paused.Progress);
        Assert.Equal(new DownloadProgress(DownloadProgressState.Paused, 400, 2000), metered.Progress);
    }
}
