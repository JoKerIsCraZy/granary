using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Where a finished or failed download is said: a short notice in the window while Granary is in front, a Windows notification
/// while it is not; and what the notification's Play and Open Downloads do.
/// </summary>
public sealed class DownloadNoticesTests : IAsyncDisposable
{
    private readonly DownloadHarness _h = new();
    private readonly FakeAppNotifications _windows = new();
    private readonly RecordingNavigation _navigation = new();
    private DownloadActivity? _activity;
    private DownloadNotices? _notices;

    public async ValueTask DisposeAsync()
    {
        _notices?.Dispose();
        _activity?.Dispose();
        await _h.DisposeAsync();
    }

    private async Task<DownloadNotices> NoticesAsync(params (string Id, DownloadState State)[] downloads)
    {
        foreach (var (id, state) in downloads)
        {
            await _h.AddAsync(id, state, mediaItemId: "mv_" + id);
        }
        var manager = _h.NewManager();
        _activity = new DownloadActivity(manager, _h.Session, new ImmediateDispatcher());
        _notices = new DownloadNotices(_activity, _windows, manager, _navigation, new ImmediateDispatcher(), _h.Time);
        _activity.Start();
        return _notices;
    }

    private Task FinishAsync(string id) => _h.Store.UpdateAsync(Scope7, id, r => r with { State = DownloadState.Completed }, Token);

    private Task FailAsync(string id) =>
        _h.Store.UpdateAsync(Scope7, id, r => r with { State = DownloadState.Failed, Failure = DownloadFailure.NotEnoughSpace }, Token);

    [Fact]
    public async Task InFront_AFinishedDownload_ShowsInTheWindow_ForFiveSeconds()
    {
        var notices = await NoticesAsync(("dl_1", DownloadState.Downloading));
        Assert.False(notices.HasNotice);

        await FinishAsync("dl_1");

        Assert.Equal(("Example Movie is ready to watch offline", true, false), (notices.NoticeText, notices.HasNotice, notices.IsWarning));
        Assert.Empty(_windows.Shown);
        _h.Time.Advance(DownloadNotices.NoticeShowsFor - TimeSpan.FromMilliseconds(1));
        Assert.True(notices.HasNotice);
        _h.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(("", false), (notices.NoticeText, notices.HasNotice));
    }

    [Fact]
    public async Task InTheBackground_ItIsAWindowsNotification_NotANoticeInTheWindow()
    {
        _windows.IsAppInFront = false;
        var notices = await NoticesAsync(("dl_1", DownloadState.Downloading));

        await FinishAsync("dl_1");

        Assert.Equal([new DownloadAnnouncement("Example Movie is ready to watch offline", false, "dl_1")], _windows.Shown);
        Assert.False(notices.HasNotice);
    }

    [Fact]
    public async Task InTheBackground_WithoutWindowsNotifications_ItShowsInTheWindow()
    {
        _windows.IsAppInFront = false;
        _windows.Takes = false;
        var notices = await NoticesAsync(("dl_1", DownloadState.Downloading));

        await FinishAsync("dl_1");

        Assert.Equal("Example Movie is ready to watch offline", notices.NoticeText);
    }

    [Fact]
    public async Task AFailure_ShowsAsAWarning()
    {
        var notices = await NoticesAsync(("dl_1", DownloadState.Downloading));

        await FailAsync("dl_1");

        Assert.Equal(("Example Movie didn't download: Not enough storage on this PC", true), (notices.NoticeText, notices.IsWarning));
    }

    [Fact]
    public async Task ANewerNotice_TakesThePlace_AndKeepsItsOwnTime()
    {
        var notices = await NoticesAsync(("dl_1", DownloadState.Downloading), ("dl_2", DownloadState.Downloading));
        await FailAsync("dl_1");
        _h.Time.Advance(TimeSpan.FromSeconds(3));

        await FinishAsync("dl_2");

        Assert.Equal(("Example Movie is ready to watch offline", false), (notices.NoticeText, notices.IsWarning));
        _h.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(notices.HasNotice);
        _h.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(notices.HasNotice);
    }

    [Fact]
    public async Task Play_PlaysTheDownloadFromThisPc()
    {
        var notices = await NoticesAsync(("dl_1", DownloadState.Completed));

        notices.Act(DownloadNotices.PlayAction, "dl_1");

        Assert.Equal([(AppRoute.Player, (object?)PlayRequest.Resume("mv_dl_1"))], _navigation.Navigations);
    }

    [Fact]
    public async Task OpenDownloads_AndAPlayOfADownloadNoLongerHere_OpenTheDownloadsPage()
    {
        var notices = await NoticesAsync(("dl_1", DownloadState.Completed), ("dl_2", DownloadState.Downloading));

        notices.Act(DownloadNotices.OpenAction, null);
        notices.Act(DownloadNotices.PlayAction, "dl_9");
        notices.Act(DownloadNotices.PlayAction, "dl_2");
        notices.Act("something else", "dl_1");

        Assert.Equal([AppRoute.Downloads, AppRoute.Downloads, AppRoute.Downloads, AppRoute.Downloads], _navigation.Sections);
        Assert.DoesNotContain(_navigation.Navigations, n => n.Route == AppRoute.Player);
    }

    [Fact]
    public async Task WithoutAProfile_ANotificationOpensNothing()
    {
        var notices = await NoticesAsync(("dl_1", DownloadState.Completed));
        notices.Act(DownloadNotices.OpenAction, null);
        Assert.Single(_navigation.Sections);
        _h.Session.State = SessionState.SignedOut;

        notices.Act(DownloadNotices.PlayAction, "dl_1");
        notices.Act(DownloadNotices.OpenAction, null);

        Assert.Single(_navigation.Sections);
        Assert.Single(_navigation.Navigations);
    }
}
