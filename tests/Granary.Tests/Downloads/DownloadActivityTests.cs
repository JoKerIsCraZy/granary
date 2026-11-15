using Granary.Core.Downloads;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The downloads outside their page: the sidebar's count of the active profile's downloads coming or waiting, whether one comes
/// now, and the taskbar button's progress, following the downloads for the app's whole run.
/// </summary>
public sealed class DownloadActivityTests : IAsyncDisposable
{
    private readonly DownloadHarness _h = new();
    private DownloadActivity? _activity;

    public async ValueTask DisposeAsync()
    {
        _activity?.Dispose();
        await _h.DisposeAsync();
    }

    private DownloadActivity Started(DownloadManager? manager = null)
    {
        _activity = new DownloadActivity(manager ?? _h.NewManager(), _h.Session, new ImmediateDispatcher());
        _activity.Start();
        return _activity;
    }

    private Task ChangeAsync(string id, Func<DownloadRecord, DownloadRecord> change, DownloadScope? scope = null) =>
        _h.Store.UpdateAsync(scope ?? Scope7, id, change, Token);

    [Fact]
    public async Task TheCount_FollowsTheDownloads_AsTheyPauseResumeAndFinish()
    {
        await _h.AddAsync("dl_1", DownloadState.Queued);
        await _h.AddAsync("dl_2", DownloadState.Completed);
        var manager = _h.NewManager();
        var activity = Started(manager);

        Assert.Equal((1, true), (activity.PendingCount, activity.HasPending));
        Assert.Equal("Downloads, 1 download in progress", activity.EntryName);

        await _h.AddAsync("dl_3", DownloadState.Queued);
        Assert.Equal(2, activity.PendingCount);
        Assert.Equal("Downloads, 2 downloads in progress", activity.EntryName);

        await manager.PauseAsync("dl_1");
        Assert.Equal(1, activity.PendingCount);

        await manager.ResumeAsync("dl_1");
        Assert.Equal(2, activity.PendingCount);

        await ChangeAsync("dl_1", r => r with { State = DownloadState.Completed });
        await ChangeAsync("dl_3", r => r with { State = DownloadState.Failed, Failure = DownloadFailure.Server });
        Assert.Equal((0, false), (activity.PendingCount, activity.HasPending));
        Assert.Equal("Downloads", activity.EntryName);
    }

    [Fact]
    public async Task IsTransferring_WhileADownloadComes_NotWhilePausedOrDone()
    {
        await _h.AddAsync("dl_1", DownloadState.Queued);
        var manager = _h.NewManager();
        var activity = Started(manager);
        Assert.False(activity.IsTransferring);

        await ChangeAsync("dl_1", r => r with { State = DownloadState.Downloading });
        Assert.True(activity.IsTransferring);

        await manager.PauseAsync("dl_1");
        Assert.False(activity.IsTransferring);

        await manager.ResumeAsync("dl_1");
        Assert.True(activity.IsTransferring);

        await ChangeAsync("dl_1", r => r with { State = DownloadState.Completed });
        Assert.False(activity.IsTransferring);
    }

    [Fact]
    public async Task AnotherProfile_CountsItsOwnDownloads()
    {
        await _h.AddAsync("dl_1", DownloadState.Queued);
        await _h.AddAsync("dl_2", DownloadState.Queued);
        await _h.AddAsync("dl_8", DownloadState.Downloading, scope: Scope8);
        var activity = Started();
        Assert.Equal((2, false), (activity.PendingCount, activity.IsTransferring));

        _h.SwitchProfile("8");

        Assert.Equal((1, true), (activity.PendingCount, activity.IsTransferring));
    }

    [Fact]
    public async Task Changes_FromOtherThreads_AreShownOnTheUiThread_OnceForABurst()
    {
        await _h.AddAsync("dl_1", DownloadState.Queued);
        var ui = new QueuedDispatcher();
        _activity = new DownloadActivity(_h.NewManager(), _h.Session, ui);
        _activity.Start();
        ui.RunAll();
        Assert.Equal(1, _activity.PendingCount);

        await _h.AddAsync("dl_2", DownloadState.Queued);
        await _h.AddAsync("dl_3", DownloadState.Queued);
        await ChangeAsync("dl_1", r => r with { State = DownloadState.Completed });

        // Nothing changes off the UI thread; the burst is one refresh there.
        Assert.Equal(1, _activity.PendingCount);
        Assert.Equal(1, ui.Pending);
        ui.RunAll();
        Assert.Equal(2, _activity.PendingCount);
    }

    [Fact]
    public async Task Disposed_ItFollowsNothing()
    {
        await _h.AddAsync("dl_1", DownloadState.Queued);
        var activity = Started();
        activity.Dispose();

        await _h.AddAsync("dl_2", DownloadState.Queued);

        Assert.Equal(1, activity.PendingCount);
    }

    [Fact]
    public async Task TheProgress_FollowsTheBytes_AsATransferGoesFurther_AndEndsWithIt()
    {
        // A UI thread of its own, as the app's: the refreshes a running transfer posts run one after another.
        using var ui = new SerialDispatcher();
        await _h.AddAsync("dl_1", DownloadState.Queued, size: 3000);
        var body = _h.ServeGated("dl_1", DownloadPayloads.Source(3000));
        var activity = new DownloadActivity(_h.NewManager(_h.StartQueue()), _h.Session, ui);
        _activity = activity;
        await ui.RunAsync(() =>
        {
            activity.Start();
            return Task.CompletedTask;
        });
        await Until(() => ui.Read(() => activity.IsTransferring), "the download came");
        Assert.Equal(new DownloadProgress(DownloadProgressState.Normal, 0, 3000), ui.Read(() => activity.Progress));

        body.Release(1800);

        await Until(() => ui.Read(() => activity.Progress.Done) == 1800, "the progress showed");
        Assert.Equal(new DownloadProgress(DownloadProgressState.Normal, 1800, 3000), ui.Read(() => activity.Progress));

        body.ReleaseAll();

        await Until(() => ui.Read(() => activity.Progress) == DownloadProgress.None, "the download finished");
        Assert.Equal((0, false), ui.Read(() => (activity.PendingCount, activity.IsTransferring)));
        Assert.Empty(ui.Failures);
    }

    [Fact]
    public async Task ThePausedProgress_ShowsWhileEveryDownloadIsPaused()
    {
        await _h.AddAsync("dl_1", DownloadState.Queued, size: 3000);
        var manager = _h.NewManager();
        var activity = Started(manager);
        Assert.Equal(DownloadProgressState.Normal, activity.Progress.State);

        await manager.PauseAsync("dl_1");
        Assert.Equal(DownloadProgressState.Paused, activity.Progress.State);

        await manager.DeleteAsync("dl_1", Token);
        Assert.Equal(DownloadProgress.None, activity.Progress);
    }

    [Fact]
    public async Task AFinishedDownload_IsAnnounced_OnceOnTheUiThread_AndWhatWasDoneAtTheStartIsNot()
    {
        await _h.AddAsync("dl_1", DownloadState.Downloading);
        await _h.AddAsync("dl_2", DownloadState.Completed, mediaItemId: "mv_2", change: r => r with { Title = "Another Movie" });
        var activity = Started();
        var announced = new List<DownloadAnnouncement>();
        activity.Announced += (_, a) => announced.Add(a);

        await ChangeAsync("dl_1", r => r with { State = DownloadState.Completed });
        await ChangeAsync("dl_1", r => r with { IsWatched = true });

        Assert.Equal([new DownloadAnnouncement("Example Movie is ready to watch offline", false, "dl_1")], announced);
    }
}
