using Granary.Core.Downloads;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The sidebar's Downloads entry: the count of the active profile's downloads coming or waiting, and whether one comes now,
/// following the downloads for the app's whole run.
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
}
