using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// A monitored series' storage limit, held on this PC as Silo's Apple app holds it, and Granary's own storage limit for
/// downloads: an episode of a monitored series that would take its downloads past either waits, and nothing is deleted for it.
/// </summary>
public sealed class DownloadQueueLimitsTests : IAsyncDisposable
{
    private const long GB = 1L << 30;

    private static readonly byte[] Source = DownloadPayloads.Source(3000);

    private readonly DownloadHarness _h = new();
    private readonly MonitorBook _book = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private DownloadQueue StartQueue()
    {
        var queue = _h.NewQueue(monitors: _book);
        queue.Start();
        return queue;
    }

    // As the server answers a change: with a validator of its own.
    private void Monitor(long maxStorageBytes) =>
        _book.Put(Scope7, new DownloadSubscription { Id = "sub_1", SeriesId = "sr_1", Mode = SubscriptionModes.Future, MaxStorageBytes = maxStorageBytes, Active = true,
            Etag = "\"m" + maxStorageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"" });

    private Task<StoredDownload> AddEpisodeAsync(string id, string episodeId, DownloadState state = DownloadState.Queued, long size = 3000) =>
        _h.AddAsync(id, state, size, mediaItemId: episodeId, change: r => r with { SeriesId = "sr_1", SeriesTitle = "Example Series" });

    [Fact]
    public async Task AnEpisodeThatWouldPassItsSeriesLimit_Waits_AndComesOnceTheresRoom()
    {
        Monitor(maxStorageBytes: 5000);
        await AddEpisodeAsync("dl_1", "ep_1", DownloadState.Completed);
        await AddEpisodeAsync("dl_2", "ep_2");
        _h.ServeFile("dl_2", Source);
        var queue = StartQueue();
        await SettleAsync(queue);

        Assert.Empty(_h.FileRequests("dl_2"));
        Assert.Equal(DownloadHold.SeriesLimit, queue.HoldOf(Scope7, "dl_2"));

        await _h.NewManager(queue).DeleteAsync("dl_1", Token);

        await Until(() => _h.Find("dl_2")?.Record.State == DownloadState.Completed, "the episode came");
    }

    [Fact]
    public async Task RaisingTheSeriesLimit_StartsTheEpisodeThatWaited()
    {
        Monitor(maxStorageBytes: 5000);
        await AddEpisodeAsync("dl_1", "ep_1", DownloadState.Completed);
        await AddEpisodeAsync("dl_2", "ep_2");
        _h.ServeFile("dl_2", Source);
        var queue = StartQueue();
        await SettleAsync(queue);
        Assert.Empty(_h.FileRequests("dl_2"));

        Monitor(maxStorageBytes: 0);

        await Until(() => _h.Find("dl_2")?.Record.State == DownloadState.Completed, "the episode came");
    }

    [Fact]
    public async Task AnEpisodeOfASeriesNoMonitorHolds_IsNotHeld()
    {
        await AddEpisodeAsync("dl_1", "ep_1", DownloadState.Completed, size: 10 * GB);
        await AddEpisodeAsync("dl_2", "ep_2");
        await _h.Preferences.SetStorageLimitGbAsync(1);
        _h.ServeFile("dl_2", Source);

        var queue = StartQueue();

        await Until(() => _h.Find("dl_2")?.Record.State == DownloadState.Completed, "the episode came");
        Assert.Equal(DownloadHold.None, queue.HoldOf(Scope7, "dl_2"));
    }

    [Fact]
    public async Task AtTheStorageLimitForDownloads_MonitoredEpisodesWait_OtherDownloadsDoNot()
    {
        Monitor(maxStorageBytes: 0);
        await _h.Preferences.SetStorageLimitGbAsync(1);
        await _h.AddAsync("dl_1", DownloadState.Completed, size: GB - 1000, mediaItemId: "mv_9");
        await AddEpisodeAsync("dl_2", "ep_2");
        await _h.AddAsync("dl_3", mediaItemId: "mv_1");
        _h.ServeFile("dl_2", Source);
        _h.ServeFile("dl_3", Source);

        var queue = StartQueue();

        await Until(() => _h.Find("dl_3")?.Record.State == DownloadState.Completed, "the film came");
        await SettleAsync(queue);
        Assert.Empty(_h.FileRequests("dl_2"));
        Assert.Equal(DownloadHold.DownloadsLimit, queue.HoldOf(Scope7, "dl_2"));
        // Nothing was deleted to make room.
        Assert.NotNull(_h.Find("dl_1"));

        await _h.Preferences.SetStorageLimitGbAsync(0);

        await Until(() => _h.Find("dl_2")?.Record.State == DownloadState.Completed, "the episode came");
    }

    [Fact]
    public async Task AHeldEpisodeWhoseRetryFellDue_DoesNotKeepTheQueueLooking()
    {
        Monitor(maxStorageBytes: 5000);
        await AddEpisodeAsync("dl_2", "ep_2");
        _h.ServeFile("dl_2", Profiles.ScriptedSilo.NetworkError());
        var queue = StartQueue();
        await Until(() => _h.FileRequests("dl_2").Count == 1, "the first try");
        await SettleAsync(queue);

        // Its retry is due while its series' limit holds it: the loop waits for a change, not for nothing.
        await AddEpisodeAsync("dl_1", "ep_1", DownloadState.Completed);
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await SettleAsync(queue);

        Assert.Single(_h.FileRequests("dl_2"));
        Assert.Equal(DownloadHold.SeriesLimit, queue.HoldOf(Scope7, "dl_2"));
    }

    [Fact]
    public async Task AHeldEpisode_SaysWhatHoldsIt()
    {
        Monitor(maxStorageBytes: 5000);
        await AddEpisodeAsync("dl_1", "ep_1", DownloadState.Completed);
        await AddEpisodeAsync("dl_2", "ep_2");
        var queue = StartQueue();
        await SettleAsync(queue);

        var item = _h.NewManager(queue).Find("dl_2")!;

        Assert.Equal(DownloadHold.SeriesLimit, item.Hold);
        Assert.Equal("Series storage limit reached", DownloadText.Status(item));
        Assert.Equal("Storage limit for downloads reached", DownloadText.Status(item with { Hold = DownloadHold.DownloadsLimit }));
    }
}
