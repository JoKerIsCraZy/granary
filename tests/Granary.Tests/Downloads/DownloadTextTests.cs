using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;

namespace Granary.Tests.Downloads;

/// <summary>What a download's status and a quality preset read like: the apps' words.</summary>
public sealed class DownloadTextTests
{
    private static DownloadItem Item(DownloadState state, double? preparing = null, long bytesDone = 0, DownloadFailure failure = DownloadFailure.None,
        bool paused = false, bool gone = false, bool missing = false, bool metered = false) =>
        new("dl_1", "ep_5", "Chapter Five", "sr_1", "Example Series", 1, 5, state, failure, paused, gone, missing, metered, ExpectedBytes: 1000,
            BytesDone: bytesDone, PreparationProgress: preparing, PosterPath: null, MediaFileId: "4567");

    [Theory]
    [InlineData(DownloadState.Preparing, 0.42, 0, DownloadFailure.None, false, false, false, false, "Preparing… 42%")]
    [InlineData(DownloadState.Preparing, null, 0, DownloadFailure.None, false, false, false, false, "Preparing…")]
    [InlineData(DownloadState.Queued, null, 0, DownloadFailure.None, false, false, false, false, "Queued")]
    [InlineData(DownloadState.Downloading, null, 500, DownloadFailure.None, false, false, false, false, "Downloading · 50%")]
    [InlineData(DownloadState.Downloading, null, 290, DownloadFailure.None, false, false, false, false, "Downloading · 29%")]
    [InlineData(DownloadState.Downloading, null, 999, DownloadFailure.None, false, false, false, false, "Downloading · 99%")]
    [InlineData(DownloadState.Downloading, null, 500, DownloadFailure.None, true, false, false, false, "Paused")]
    [InlineData(DownloadState.Queued, null, 0, DownloadFailure.None, false, false, false, true, "Paused · metered connection")]
    [InlineData(DownloadState.Downloading, null, 0, DownloadFailure.None, false, false, false, true, "Downloading · 0%")]
    [InlineData(DownloadState.Completed, null, 1000, DownloadFailure.None, false, false, false, false, "Downloaded")]
    [InlineData(DownloadState.Failed, null, 0, DownloadFailure.Server, false, false, false, false, "Failed")]
    [InlineData(DownloadState.Failed, null, 0, DownloadFailure.Integrity, false, false, false, false, "Failed")]
    [InlineData(DownloadState.Failed, null, 0, DownloadFailure.NotEnoughSpace, false, false, false, false, "Failed · not enough space")]
    [InlineData(DownloadState.Completed, null, 1000, DownloadFailure.None, false, true, false, false, "No longer available")]
    [InlineData(DownloadState.Queued, null, 0, DownloadFailure.None, true, true, false, false, "No longer available")]
    [InlineData(DownloadState.Completed, null, 1000, DownloadFailure.None, false, true, true, false, "Missing file")]
    public void Status_UsesTheAppsWords(DownloadState state, double? preparing, long bytesDone, DownloadFailure failure, bool paused, bool gone,
        bool missing, bool metered, string expected) =>
        Assert.Equal(expected, DownloadText.Status(Item(state, preparing, bytesDone, failure, paused, gone, missing, metered)));

    [Theory]
    [InlineData("original", null, null, "Original")]
    [InlineData("10mbps", 10000, 1080, "10 Mbps · up to 1080p")]
    [InlineData("20mbps", 20000, 2160, "20 Mbps · up to 2160p")]
    [InlineData("2mbps", 1500, 720, "1.5 Mbps · up to 720p")]
    [InlineData("5mbps", 5000, null, "5 Mbps")]
    [InlineData("1mbps", null, null, "1 Mbps")]
    public void QualityLabel_IsOriginalOrMbpsUpToHeight(string preset, int? kbps, int? height, string expected) =>
        Assert.Equal(expected, DownloadText.QualityLabel(new DownloadQualityOption(preset, kbps, height)));

    [Fact]
    public void Progress_IsWholeOnceDownloaded_AndOnlyThenPlayable()
    {
        Assert.Equal(0.5, Item(DownloadState.Downloading, bytesDone: 500).Progress);
        Assert.Equal(1, Item(DownloadState.Completed).Progress);
        Assert.True(Item(DownloadState.Completed).IsPlayable);
        Assert.False(Item(DownloadState.Completed, missing: true).IsPlayable);
        Assert.False(Item(DownloadState.Downloading, bytesDone: 1000).IsPlayable);
    }

    // The Apple app's speed, in the size the page says everywhere.
    [Theory]
    [InlineData(12_897_484, "12.3 MB/s")]
    [InlineData(1_048_576, "1.0 MB/s")]
    [InlineData(512_000, "500.0 KB/s")]
    [InlineData(800, "800 B/s")]
    public void Speed_IsTheSizeASecond(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, DownloadText.Speed(bytesPerSecond));

    // The Apple app's time left: whole minutes, rounded; hours past the hour.
    [Theory]
    [InlineData(29, "under 1 min left")]
    [InlineData(31, "1 min left")]
    [InlineData(4 * 60 + 29, "4 min left")]
    [InlineData(59 * 60 + 29, "59 min left")]
    [InlineData(59 * 60 + 31, "1 hr left")]
    [InlineData(65 * 60, "1 hr 5 min left")]
    [InlineData(150 * 60, "2 hr 30 min left")]
    public void TimeLeft_IsInMinutes_AndHoursPastTheHour(int seconds, string expected) =>
        Assert.Equal(expected, DownloadText.TimeLeft(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ADownloadingStatus_AddsTheSpeedAndTheTimeLeft_OnceTheyAreKnown()
    {
        // 420 of 1000 bytes, 580 to go at 2 bytes a second: 290 s.
        var downloading = Item(DownloadState.Downloading, bytesDone: 420);

        Assert.Equal("Downloading · 42% · 2 B/s · 5 min left", DownloadText.Status(downloading, bytesPerSecond: 2));
        Assert.Equal("Downloading · 42%", DownloadText.Status(downloading, bytesPerSecond: null));
        Assert.Equal("Downloading · 42%", DownloadText.Status(downloading, bytesPerSecond: 0.5));
        // Only a download that comes has a speed.
        Assert.Equal("Paused", DownloadText.Status(Item(DownloadState.Downloading, bytesDone: 420, paused: true), bytesPerSecond: 2));
        Assert.Equal("Queued", DownloadText.Status(Item(DownloadState.Queued, bytesDone: 420), bytesPerSecond: 2));
    }
}
