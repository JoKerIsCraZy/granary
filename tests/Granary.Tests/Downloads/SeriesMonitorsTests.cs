using System.Net;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Series monitors as Silo's Apple app keeps them (downloads-api.md, 8): a run reads the monitors, syncs each active one and
/// takes the episodes the server registered into the downloads; a monitor is created once, changed and stopped under its
/// validator; runs come at the start, on a profile switch, every four hours and when asked.
/// </summary>
public sealed class SeriesMonitorsTests : IAsyncDisposable
{
    private const string MonitorsPath = "api/v2/downloads/subscriptions";
    private const string SyncPath = "api/v2/downloads/subscriptions/sync";
    private const string Monitor1Path = "api/v2/downloads/subscriptions/sub_1";

    private readonly DownloadHarness _h = new();
    private readonly MonitorBook _book = new();
    private SeriesMonitors? _monitors;

    public SeriesMonitorsTests()
    {
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Capability()));
        _h.List();
    }

    public async ValueTask DisposeAsync()
    {
        _monitors?.Dispose();
        await _h.DisposeAsync();
    }

    private SeriesMonitors Monitors => _monitors ??= NewMonitors();

    private SeriesMonitors NewMonitors(MonitorPendingFile? pending = null) => new(new DownloadSubscriptionsApi(_h.Silo.Http), _h.Api, _h.Store, _book,
        _h.NewManager(), _h.Session, _h.Time, _h.Logs.For<SeriesMonitors>(), pending: pending);

    private string PendingPath => Path.Combine(_h.Directory, "data", "monitor-pending.json");

    private MonitorPendingFile PendingFile(string? path = null) => new(path ?? PendingPath, _h.Logs.For<MonitorPendingFile>());

    private void ListMonitors(params string[] monitors) =>
        _h.Silo.On(HttpMethod.Get, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page(null, monitors)));

    private void SyncAnswers(params string[] pages) =>
        _h.Silo.On(HttpMethod.Post, SyncPath, [.. pages.Select(page => ScriptedSilo.Json(HttpStatusCode.OK, page))]);

    // A registry entry of episode ep_9 of sr_1 that a sync registered, with its manifest.
    private void Registered(string id = "dl_9", string episodeId = "ep_9", int episode = 9, string status = "ready")
    {
        _h.ServeAssets(id);
        _h.Silo.On(HttpMethod.Get, ManifestPath(id), ScriptedSilo.Json(HttpStatusCode.OK,
            DownloadPayloads.Manifest(id, episodeId: episodeId, episode: episode, title: "Chapter " + episode)));
    }

    [Fact]
    public async Task ARun_SyncsEachActiveMonitor_AndKeepsTheEpisodesItRegistered()
    {
        ListMonitors(MonitorPayloads.Monitor("sub_1", "sr_1"), MonitorPayloads.Monitor("sub_2", "sr_2", etag: "\"m2\"", active: false));
        SyncAnswers(MonitorPayloads.Sync("sub_1", registered: 1));
        Registered();
        _h.List(DownloadPayloads.Entry("dl_9", "ready", episodeId: "ep_9"), DownloadPayloads.Entry("dl_8", "ready"));

        await Monitors.SyncAsync(Token);

        Assert.Equal(["sub_1", "sub_2"], Monitors.Monitors.Select(m => m.Id));
        var sync = Assert.Single(_h.Silo.SentTo(HttpMethod.Post, SyncPath));
        Assert.Contains("\"subscription_id\":\"sub_1\"", sync.Body, StringComparison.Ordinal);
        Assert.Equal("7", sync.AsProfile);
        var kept = _h.Find("dl_9")!.Record;
        Assert.Equal(("ep_9", "sr_1", "Chapter 9", "Example Series", 1, 9), (kept.MediaItemId, kept.SeriesId, kept.Title, kept.SeriesTitle,
            kept.SeasonNumber!.Value, kept.EpisodeNumber!.Value));
        Assert.Equal(DownloadState.Queued, kept.State);
        // A film's entry from elsewhere is no monitor's.
        Assert.Null(_h.Find("dl_8"));
    }

    [Fact]
    public async Task ASyncThatGoesOn_FollowsItsCursor_UnderTheSameValidator()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync(registered: 0, nextCursor: "n2"), MonitorPayloads.Sync(registered: 1));

        await Monitors.SyncAsync(Token);

        var pages = _h.Silo.SentTo(HttpMethod.Post, SyncPath);
        Assert.Equal(2, pages.Count);
        Assert.Equal(pages[0].Body, pages[1].Body);
        Assert.Equal("?limit=100&cursor=n2", pages[1].Uri.Query);
    }

    [Fact]
    public async Task ASyncPageThatGotNoAnswer_IsSentAgainAsItWas_ASecondLater()
    {
        ListMonitors(MonitorPayloads.Monitor());
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Problem(502, "bad_gateway"), ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Sync()));

        var run = Monitors.SyncAsync(Token);
        await Until(() => _h.Time.NextDue == SeriesMonitors.PageRetryDelays[0], "the wait before the page goes again");
        Assert.Single(_h.Silo.SentTo(HttpMethod.Post, SyncPath));
        _h.Time.Advance(SeriesMonitors.PageRetryDelays[0]);
        await run;

        var pages = _h.Silo.SentTo(HttpMethod.Post, SyncPath);
        Assert.Equal(2, pages.Count);
        Assert.Equal((pages[0].Body, pages[0].Uri), (pages[1].Body, pages[1].Uri));
    }

    [Fact]
    public async Task AMonitorThatChangedWhileItSynced_IsReadAgain_AndItsSyncStartsOverUnderItsNewValidator()
    {
        ListMonitors(MonitorPayloads.Monitor());
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Problem(409, "conflict"), ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Sync()));
        _h.Silo.On(HttpMethod.Get, Monitor1Path, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m2\"", mode: "all")));

        await Monitors.SyncAsync(Token);

        var pages = _h.Silo.SentTo(HttpMethod.Post, SyncPath);
        Assert.Equal(2, pages.Count);
        Assert.Contains("m2", pages[1].Body, StringComparison.Ordinal);
        Assert.Equal(SubscriptionModes.All, Assert.Single(Monitors.Monitors).Mode);
    }

    [Fact]
    public async Task AMonitorTheServerNoLongerHas_LeavesTheList()
    {
        ListMonitors(MonitorPayloads.Monitor());
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Problem(404, "not_found"));
        _h.Silo.On(HttpMethod.Get, Monitor1Path, ScriptedSilo.Problem(404, "not_found"));

        await Monitors.SyncAsync(Token);

        Assert.Empty(Monitors.Monitors);
    }

    [Fact]
    public async Task ARun_SendsNothingAsItsProfile_OnceAnotherAccountIsSignedIn()
    {
        _h.Silo.On(HttpMethod.Get, MonitorsPath, () =>
        {
            // While the list is answered, another account signs in on the server; its profile is 7 too.
            _h.Silo.ActingAccount = () => "2";
            return ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page(null, MonitorPayloads.Monitor()))();
        });
        SyncAnswers(MonitorPayloads.Sync());

        await Monitors.SyncAsync(Token);

        var list = Assert.Single(_h.Silo.SentTo(HttpMethod.Get, MonitorsPath));
        Assert.Equal(("1", "7"), (list.AsAccount, list.AsProfile));
        // The monitor's sync would register episodes for the other account's profile: it is not sent.
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Post, SyncPath));
    }

    [Fact]
    public async Task EpisodesThePcKeepsOrIsDeleting_AreNotTakenAgain()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync(registered: 0));
        await _h.AddAsync("dl_5", mediaItemId: "ep_5", change: r => r with { SeriesId = "sr_1" });
        await _h.AddAsync("dl_6", mediaItemId: "ep_6", change: r => r with { SeriesId = "sr_1" });
        await _h.Store.RemoveAsync(Scope7, "dl_6", queueServerDelete: true, Token);
        Registered("dl_7", "ep_5", 5);
        _h.List(DownloadPayloads.Entry("dl_5", "ready", episodeId: "ep_5"), DownloadPayloads.Entry("dl_6", "ready", episodeId: "ep_6"),
            DownloadPayloads.Entry("dl_7", "ready", episodeId: "ep_5"), DownloadPayloads.Entry("dl_10", "failed", episodeId: "ep_10"));

        await Monitors.SyncAsync(Token);

        Assert.Equal(["dl_5"], _h.Store.In(Scope7).Select(d => d.Record.DownloadId));
    }

    [Fact]
    public async Task DeleteAfterWatching_DeletesTheSeriesFinishedEpisodes_AtTheEndOfARun()
    {
        ListMonitors(MonitorPayloads.Monitor("sub_1", "sr_1", deleteWatched: true), MonitorPayloads.Monitor("sub_2", "sr_2", etag: "\"m2\""));
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Sync("sub_1")),
            ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Sync("sub_2")));
        await _h.AddPlayableAsync("dl_1", "ep_1", 1, change: r => r with { IsWatched = true });
        await _h.AddPlayableAsync("dl_2", "ep_2", 2);
        await _h.AddPlayableAsync("dl_3", "ep_3", 3, change: r => r with { SeriesId = "sr_2", IsWatched = true });
        await _h.AddPlayableAsync("dl_4", "ep_4", 4, change: r => r with { IsWatched = true });
        using var playing = _h.Playing.Enter(Scope7, "dl_4");

        await Monitors.SyncAsync(Token);

        // Watched and of a series that deletes them; not one that plays now, nor another series'.
        Assert.Equal(["dl_2", "dl_3", "dl_4"], _h.Store.In(Scope7).Select(d => d.Record.DownloadId).Order());
        Assert.Contains(_h.Store.PendingDeletes(Scope7), d => d.DownloadId == "dl_1");
    }

    [Fact]
    public async Task AMonitorThatChangedUnderItsSync_AndCouldNotBeReadAgain_DeletesNothing()
    {
        ListMonitors(MonitorPayloads.Monitor(deleteWatched: true));
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Problem(409, "conflict"));
        _h.Silo.On(HttpMethod.Get, Monitor1Path, ScriptedSilo.NetworkError());
        await _h.AddPlayableAsync("dl_1", "ep_1", 1, change: r => r with { IsWatched = true });

        await Monitors.SyncAsync(Token);

        // What changed may be Delete after watching itself: nothing goes on the word of the monitor as it was.
        Assert.NotNull(_h.Find("dl_1"));
    }

    [Fact]
    public async Task AMonitorThatKeptChangingUnderItsSync_DeletesNothing()
    {
        ListMonitors(MonitorPayloads.Monitor(deleteWatched: true));
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Problem(409, "conflict"));
        _h.Silo.On(HttpMethod.Get, Monitor1Path, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m2\"", deleteWatched: true)));
        await _h.AddPlayableAsync("dl_1", "ep_1", 1, change: r => r with { IsWatched = true });

        await Monitors.SyncAsync(Token);

        Assert.Equal(3, _h.Silo.SentTo(HttpMethod.Post, SyncPath).Count);
        Assert.NotNull(_h.Find("dl_1"));
    }

    [Fact]
    public async Task ARunThatCouldNotReadTheMonitors_TriesAgainAMinuteLater_AndOnlyAfterAFewSaysThePolicyIsAsKnownAsItGets()
    {
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Problem(503, "dependency_unavailable"));
        Monitors.Start();

        for (int run = 1; run < SeriesMonitors.UnreadRuns; run++)
        {
            if (run > 1)
            {
                _h.Time.Advance(SeriesMonitors.UnreadRetry);
            }
            _h.Time.Advance(SeriesMonitors.Settle);
            await Until(() => _h.Silo.SentTo(HttpMethod.Get, CapabilityPath).Count == run, "a run");
            await Monitors.LatestRun;

            // The queue still holds the episodes: a read that failed says nothing of the monitors.
            Assert.False(_book.IsKnown(Scope7));
        }

        _h.Time.Advance(SeriesMonitors.UnreadRetry);
        _h.Time.Advance(SeriesMonitors.Settle);
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, CapabilityPath).Count == SeriesMonitors.UnreadRuns, "the last try");
        await Monitors.LatestRun;

        // The episodes the queue held go: no limit is known, and none will be before the server answers.
        Assert.True(_book.IsKnown(Scope7));
        Assert.Empty(_book.In(Scope7));
        // And no more tries come before the four hours' run.
        _h.Time.Advance(SeriesMonitors.UnreadRetry);
        _h.Time.Advance(SeriesMonitors.Settle);
        await Monitors.LatestRun;
        Assert.Equal(SeriesMonitors.UnreadRuns, _h.Silo.SentTo(HttpMethod.Get, CapabilityPath).Count);
    }

    [Fact]
    public async Task AServerThatDoesNotOfferDownloads_SaysThePolicyIsKnownAtOnce()
    {
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability(enabled: false)));

        await Monitors.SyncAsync(Token);

        Assert.True(_book.IsKnown(Scope7));
    }

    [Fact]
    public async Task AMonitorMadeBeforeARunReadTheList_LeavesThePolicyUnknownUntilOneDoes()
    {
        _h.Silo.On(HttpMethod.Post, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor()));
        SyncAnswers(MonitorPayloads.Sync(registered: 0));

        await Monitors.CreateAsync("sr_1", "Example Series", new MonitorChoice(SubscriptionModes.Future, false, 0, null), Token);

        // The other monitors of the profile, and their limits, are not known from one.
        Assert.False(_book.IsKnown(Scope7));

        ListMonitors(MonitorPayloads.Monitor());
        await Monitors.SyncAsync(Token);

        Assert.True(_book.IsKnown(Scope7));
    }

    [Fact]
    public async Task WhileTheServerDoesNotMonitorSeries_NothingIsSynced()
    {
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Capability(monitoring: false)));
        ListMonitors(MonitorPayloads.Monitor());

        await Monitors.SyncAsync(Token);

        Assert.Empty(_h.Silo.SentTo(HttpMethod.Post, SyncPath));
        // A monitor that is there still shows, so it can be stopped.
        Assert.Single(Monitors.Monitors);
    }

    [Fact]
    public async Task Create_SendsOnce_KeepsTheMonitor_AndSyncsIt()
    {
        ListMonitors();
        _h.Silo.On(HttpMethod.Post, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(deleteWatched: true, maxStorageBytes: 10)));
        SyncAnswers(MonitorPayloads.Sync(registered: 0));

        var result = await Monitors.CreateAsync("sr_1", "Example Series", new MonitorChoice(SubscriptionModes.Future, DeleteWatched: true,
            MaxStorageBytes: 10, Quality: null), Token);

        Assert.Equal(MonitorResult.Saved, result);
        var create = Assert.Single(_h.Silo.SentTo(HttpMethod.Post, MonitorsPath));
        Assert.Equal("""{"series_id":"sr_1","mode":"future","delete_watched":true,"max_storage_bytes":10}""", create.Body);
        Assert.Equal("sub_1", Monitors.For("sr_1")?.Id);
        Assert.Equal("Example Series", _book.TitleOf(Scope7, "sr_1"));
        Assert.Single(_h.Silo.SentTo(HttpMethod.Post, SyncPath));
    }

    // Sent, and the connection ended before an answer came.
    private static Func<Task<HttpResponseMessage>> NoAnswer() =>
        () => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "The answer broke off."));

    [Fact]
    public async Task Create_ThatGotNoAnswer_IsLookedUpInTheList_NeverSentAgain()
    {
        _h.Silo.On(HttpMethod.Post, MonitorsPath, NoAnswer());
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());

        var result = await Monitors.CreateAsync("sr_1", "Example Series", new MonitorChoice(SubscriptionModes.Future, false, 0, null), Token);

        Assert.Equal(MonitorResult.Saved, result);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Post, MonitorsPath));
        Assert.Equal("sub_1", Monitors.For("sr_1")?.Id);

        ListMonitors();
        var unknown = await Monitors.CreateAsync("sr_2", "Example Two", new MonitorChoice(SubscriptionModes.Future, false, 0, null), Token);

        Assert.Equal(MonitorResult.Uncertain, unknown);
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Post, MonitorsPath).Count);
    }

    [Fact]
    public async Task ASeriesMonitoredAgain_WhileItsStopWaits_KeepsItsMonitor_AlsoWhenTheCreatesAnswerIsLost()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.NetworkError());
        await Monitors.StopAsync("sub_1", Token);
        _h.Silo.On(HttpMethod.Post, MonitorsPath, NoAnswer());

        var result = await Monitors.CreateAsync("sr_1", "Example Series", new MonitorChoice(SubscriptionModes.Future, false, 0, null), Token);
        await Monitors.SyncAsync(Token);

        // The server still has it, as the stop never reached it: it is the series' monitor again, and its stop is not sent.
        Assert.Equal(MonitorResult.Saved, result);
        Assert.Equal("sub_1", Monitors.For("sr_1")?.Id);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Delete, Monitor1Path));
    }

    [Fact]
    public async Task ASeriesMonitoredAgainWithoutDeleteAfterWatching_DeletesNothing_WhileTheServerStillSaysOtherwise()
    {
        ListMonitors(MonitorPayloads.Monitor(deleteWatched: true));
        SyncAnswers(MonitorPayloads.Sync());
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.NetworkError());
        await Monitors.StopAsync("sub_1", Token);
        _h.Silo.On(HttpMethod.Post, MonitorsPath, NoAnswer());
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.NetworkError());
        await _h.AddPlayableAsync("dl_1", "ep_1", 1, change: r => r with { IsWatched = true });

        // Monitored again without Delete after watching; the change to the monitor the server still has did not go.
        var result = await Monitors.CreateAsync("sr_1", "Example Series", new MonitorChoice(SubscriptionModes.Future, false, 0, null), Token);
        await Monitors.SyncAsync(Token);

        Assert.NotEqual(MonitorResult.Saved, result);
        Assert.NotNull(_h.Find("dl_1"));

        // Once a run reads it as the viewer asked, it is the series' monitor as any other.
        ListMonitors(MonitorPayloads.Monitor(etag: "\"m2\"", deleteWatched: false));
        await Monitors.SyncAsync(Token);
        ListMonitors(MonitorPayloads.Monitor(etag: "\"m3\"", deleteWatched: true));
        await Monitors.SyncAsync(Token);

        Assert.Null(_h.Find("dl_1"));
    }

    [Fact]
    public async Task AChoiceTheServerDidNotTake_StillHolds_WhenTheServerPausesTheMonitor()
    {
        ListMonitors(MonitorPayloads.Monitor(deleteWatched: true));
        SyncAnswers(MonitorPayloads.Sync());
        _h.Silo.On(HttpMethod.Post, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(deleteWatched: true)));
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.NetworkError());
        await _h.AddPlayableAsync("dl_1", "ep_1", 1, change: r => r with { IsWatched = true });
        await Monitors.CreateAsync("sr_1", "Example Series", new MonitorChoice(SubscriptionModes.Future, false, 0, null), Token);

        // The server paused it (its storage was revoked): its validator moved, its Delete after watching is still the old one.
        ListMonitors(MonitorPayloads.Monitor(etag: "\"m9\"", deleteWatched: true, active: false));
        await Monitors.SyncAsync(Token);

        Assert.NotNull(_h.Find("dl_1"));
    }

    [Fact]
    public async Task AChoiceTheServerDidNotTake_GivesWayToALaterChangeOfDeleteAfterWatchingItself()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        _h.Silo.On(HttpMethod.Post, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor()));
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.NetworkError());
        await _h.AddPlayableAsync("dl_1", "ep_1", 1, change: r => r with { IsWatched = true });
        await Monitors.CreateAsync("sr_1", "Example Series", new MonitorChoice(SubscriptionModes.All, false, 0, null), Token);

        // Delete after watching was turned on after the viewer's choice, which did not touch it: that newer word holds.
        ListMonitors(MonitorPayloads.Monitor(etag: "\"m9\"", deleteWatched: true));
        await Monitors.SyncAsync(Token);

        Assert.Null(_h.Find("dl_1"));
    }

    [Fact]
    public async Task ASeriesWithAMonitorTheRunCouldNotBeSureOf_DeletesNothing_WhateverItsOtherMonitorsSay()
    {
        ListMonitors(MonitorPayloads.Monitor("sub_1", "sr_1", deleteWatched: true), MonitorPayloads.Monitor("sub_2", "sr_1", etag: "\"n1\"", deleteWatched: true));
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Problem(409, "conflict"), ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Sync("sub_2")));
        _h.Silo.On(HttpMethod.Get, Monitor1Path, ScriptedSilo.NetworkError());
        await _h.AddPlayableAsync("dl_1", "ep_1", 1, change: r => r with { IsWatched = true });

        await Monitors.SyncAsync(Token);

        Assert.NotNull(_h.Find("dl_1"));
    }

    [Fact]
    public async Task Create_ThatCouldNotConnect_SentNothing_AndFails()
    {
        _h.Silo.On(HttpMethod.Post, MonitorsPath, ScriptedSilo.NetworkError());
        ListMonitors(MonitorPayloads.Monitor());

        var result = await Monitors.CreateAsync("sr_1", "Example Series", new MonitorChoice(SubscriptionModes.Future, false, 0, null), Token);

        Assert.Equal(MonitorResult.Failed, result);
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Get, MonitorsPath));
    }

    [Fact]
    public async Task Update_OfAMonitorThatChanged_WhoseReadFailed_IsNotTakenAsRead()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.Problem(412, "precondition_failed"));
        _h.Silo.On(HttpMethod.Get, Monitor1Path, ScriptedSilo.NetworkError());

        var result = await Monitors.UpdateAsync("sub_1", new MonitorEdit(SubscriptionModes.All, false, 0, null), Token);

        Assert.Equal(MonitorResult.Uncertain, result);
    }

    [Fact]
    public async Task AListThatCouldNotBeRead_TakesAndDeletesNothing()
    {
        ListMonitors(MonitorPayloads.Monitor(deleteWatched: true));
        SyncAnswers(MonitorPayloads.Sync());
        await Monitors.SyncAsync(Token);
        await _h.AddPlayableAsync("dl_1", "ep_1", 1, change: r => r with { IsWatched = true });
        Registered();
        _h.List(DownloadPayloads.Entry("dl_9", "ready", episodeId: "ep_9"));
        _h.Silo.On(HttpMethod.Get, MonitorsPath, ScriptedSilo.Problem(503, "dependency_unavailable"));

        await Monitors.SyncAsync(Token);

        // Another client may have stopped the monitor or turned Delete after watching off.
        Assert.NotNull(_h.Find("dl_1"));
        Assert.Null(_h.Find("dl_9"));
    }

    [Fact]
    public async Task WishesInQuickSuccession_AreOneRun_ThatTheyDoNotPutOff()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        Monitors.Start();

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        Monitors.SyncSoon();
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        Monitors.SyncSoon();

        await Until(() => _h.Silo.SentTo(HttpMethod.Get, MonitorsPath).Count == 1, "the run");
        await Monitors.LatestRun;
        Assert.Single(_h.Silo.SentTo(HttpMethod.Get, MonitorsPath));
    }

    [Fact]
    public async Task Create_AnsweredWithTheSeriesMonitorAsItWas_SendsWhatDiffers()
    {
        _h.Silo.On(HttpMethod.Post, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(mode: "future", active: false)));
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(mode: "all", etag: "\"m2\"")));
        SyncAnswers(MonitorPayloads.Sync());

        var result = await Monitors.CreateAsync("sr_1", "Example Series", new MonitorChoice(SubscriptionModes.All, false, 0, null), Token);

        Assert.Equal(MonitorResult.Saved, result);
        var patch = Assert.Single(_h.Silo.SentTo(HttpMethod.Patch, Monitor1Path));
        Assert.Equal("\"m1\"", patch.IfMatch);
        Assert.Equal("""{"mode":"all","delete_watched":false,"max_storage_bytes":0,"active":true}""", patch.Body);
        Assert.Equal("\"m2\"", Monitors.For("sr_1")?.Etag);
    }

    [Fact]
    public async Task Update_OfAMonitorThatChangedElsewhere_ReadsItAgain_SaysSo_AndSendsNothingMore()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.Problem(412, "precondition_failed"));
        _h.Silo.On(HttpMethod.Get, Monitor1Path, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m9\"", deleteWatched: true)));

        var result = await Monitors.UpdateAsync("sub_1", new MonitorEdit(SubscriptionModes.All, false, 0, null), Token);

        Assert.Equal(MonitorResult.Changed, result);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Patch, Monitor1Path));
        var reread = Monitors.For("sr_1")!;
        Assert.Equal(("\"m9\"", true, SubscriptionModes.Future), (reread.Etag, reread.DeleteWatched, reread.Mode));
    }

    [Fact]
    public async Task Update_OfAMonitorThatChangedSinceTheViewerSawIt_SendsNothing()
    {
        ListMonitors(MonitorPayloads.Monitor(etag: "\"m2\""));
        SyncAnswers(MonitorPayloads.Sync());
        await Monitors.SyncAsync(Token);

        var result = await Monitors.UpdateAsync("sub_1", new MonitorEdit(null, true, 0, null) { Etag = "\"m1\"" }, Token);

        Assert.Equal(MonitorResult.Changed, result);
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Patch, Monitor1Path));
    }

    [Fact]
    public async Task Update_SendsTheChoicesUnderTheValidator_AndSyncsTheMonitor()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m2\"", quality: "10mbps")));

        var result = await Monitors.UpdateAsync("sub_1", new MonitorEdit(null, false, 0, "10mbps"), Token);

        Assert.Equal(MonitorResult.Saved, result);
        var patch = Assert.Single(_h.Silo.SentTo(HttpMethod.Patch, Monitor1Path));
        Assert.Equal(("\"m1\"", """{"delete_watched":false,"max_storage_bytes":0,"active":true,"quality":"10mbps"}"""), (patch.IfMatch, patch.Body));
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Post, SyncPath).Count);
    }

    [Fact]
    public async Task Stop_LeavesTheListAtOnce_AndDeletesUnderTheValidator_ReadAgainWhenItChanged()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.Problem(412, "precondition_failed"), ScriptedSilo.NoContent());
        _h.Silo.On(HttpMethod.Get, Monitor1Path, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m2\"")));

        await Monitors.StopAsync("sub_1", Token);

        Assert.Empty(Monitors.Monitors);
        var deletes = _h.Silo.SentTo(HttpMethod.Delete, Monitor1Path);
        Assert.Equal(["\"m1\"", "\"m2\""], deletes.Select(d => d.IfMatch));
    }

    [Theory]
    [InlineData(409, "conflict")]
    [InlineData(412, "precondition_failed")]
    public async Task AStopWhoseMonitorChangedAgainUnderItsSecondTry_StillGoes_AndTheListDoesNotBringItBackMeanwhile(int status, string code)
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        _monitors = NewMonitors(PendingFile());
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.Problem(412, "precondition_failed"), ScriptedSilo.Problem(status, code),
            ScriptedSilo.NetworkError(), ScriptedSilo.NoContent());
        _h.Silo.On(HttpMethod.Get, Monitor1Path, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m2\"")));

        await Monitors.StopAsync("sub_1", Token);
        await Monitors.PendingSaved;
        Monitors.Dispose();
        // Granary starts again; the server still has the monitor, and the next run cannot reach it for the stop.
        _monitors = NewMonitors(PendingFile());
        await Monitors.SyncAsync(Token);

        Assert.Empty(Monitors.Monitors);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Post, SyncPath));

        ListMonitors();
        await Monitors.SyncAsync(Token);

        // From then on it goes under the validator read last, which spares the server a refusal each time.
        Assert.Equal(["\"m1\"", "\"m2\"", "\"m2\"", "\"m2\""], _h.Silo.SentTo(HttpMethod.Delete, Monitor1Path).Select(d => d.IfMatch));
        Assert.Empty(Monitors.Monitors);
    }

    [Fact]
    public async Task Stop_ThatDidNotReachTheServer_GoesWithTheNextRun_AndTheListDoesNotBringItBack()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.NetworkError(), ScriptedSilo.NetworkError(), ScriptedSilo.NoContent());

        await Monitors.StopAsync("sub_1", Token);
        await Monitors.SyncAsync(Token);

        // The server still lists it while its delete waits: it does not come back, and is not synced.
        Assert.Empty(Monitors.Monitors);
        Assert.Equal(2, _h.Silo.SentTo(HttpMethod.Delete, Monitor1Path).Count);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Post, SyncPath));

        ListMonitors();
        await Monitors.SyncAsync(Token);

        Assert.Equal(3, _h.Silo.SentTo(HttpMethod.Delete, Monitor1Path).Count);
        Assert.Empty(Monitors.Monitors);
    }

    [Fact]
    public async Task AStopThatDidNotReachTheServer_IsSentAfterARestart_AndTheListDoesNotBringItBackMeanwhile()
    {
        string path = PendingPath;
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        _monitors = NewMonitors(PendingFile());
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.NetworkError());
        await Monitors.StopAsync("sub_1", Token);
        await Monitors.PendingSaved;
        Monitors.Dispose();

        // Granary starts again, still without reaching the server for the stop: the list does not bring the monitor back.
        _monitors = NewMonitors(PendingFile());
        await Monitors.SyncAsync(Token);
        Assert.Empty(Monitors.Monitors);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Post, SyncPath));

        // Then it reaches it: the stop goes under the validator it was made with, and is forgotten on disk too.
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.NoContent());
        ListMonitors();
        await Monitors.SyncAsync(Token);
        await Monitors.PendingSaved;

        Assert.Equal("\"m1\"", _h.Silo.SentTo(HttpMethod.Delete, Monitor1Path)[^1].IfMatch);
        // None is left to send: the file goes.
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AStopFromBeforeARestart_WhoseMonitorTheServerPausedSince_GoesUnderItsValidatorReadAgain()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        _monitors = NewMonitors(PendingFile());
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.NetworkError());
        await Monitors.StopAsync("sub_1", Token);
        await Monitors.PendingSaved;
        Monitors.Dispose();

        // A monitor is this PC's alone: the server paused it meanwhile (its storage was revoked), and the stop still goes.
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.Problem(412, "precondition_failed"), ScriptedSilo.NoContent());
        _h.Silo.On(HttpMethod.Get, Monitor1Path, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(etag: "\"m2\"", active: false)));
        ListMonitors();
        _monitors = NewMonitors(PendingFile());
        await Monitors.SyncAsync(Token);
        await Monitors.PendingSaved;

        Assert.Equal(["\"m1\"", "\"m1\"", "\"m2\""], _h.Silo.SentTo(HttpMethod.Delete, Monitor1Path).Select(d => d.IfMatch));
        Assert.Empty(Monitors.Monitors);
        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public async Task AChoiceTheServerDidNotTake_StillHoldsAfterARestart()
    {
        ListMonitors(MonitorPayloads.Monitor(deleteWatched: true));
        SyncAnswers(MonitorPayloads.Sync());
        _h.Silo.On(HttpMethod.Post, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(deleteWatched: true)));
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.NetworkError());
        await _h.AddPlayableAsync("dl_1", "ep_1", 1, change: r => r with { IsWatched = true });
        _monitors = NewMonitors(PendingFile());
        await Monitors.CreateAsync("sr_1", "Example Series", new MonitorChoice(SubscriptionModes.Future, false, 0, null), Token);
        await Monitors.PendingSaved;
        Monitors.Dispose();

        // Granary starts again while the server still says Delete after watching: what the viewer turned off stays off.
        _monitors = NewMonitors(PendingFile());
        await Monitors.SyncAsync(Token);

        Assert.NotNull(_h.Find("dl_1"));
    }

    [Fact]
    public async Task WhileTheFileOfWritesStillToSendCannotBeRead_NothingIsDeletedSentOrWrittenOverIt()
    {
        ListMonitors(MonitorPayloads.Monitor(deleteWatched: true));
        SyncAnswers(MonitorPayloads.Sync());
        _h.Silo.On(HttpMethod.Post, MonitorsPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Monitor(deleteWatched: true)));
        _h.Silo.On(HttpMethod.Patch, Monitor1Path, ScriptedSilo.NetworkError());
        await _h.AddPlayableAsync("dl_1", "ep_1", 1, change: r => r with { IsWatched = true });
        _monitors = NewMonitors(PendingFile());
        await Monitors.CreateAsync("sr_1", "Example Series", new MonitorChoice(SubscriptionModes.Future, false, 0, null), Token);
        await Monitors.PendingSaved;
        Monitors.Dispose();
        byte[] kept = File.ReadAllBytes(PendingPath);

        // Granary starts again while another program holds the file, and the server still says Delete after watching.
        _monitors = NewMonitors(PendingFile());
        using (new FileStream(PendingPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Monitors.SyncAsync(Token);
            Assert.Equal(MonitorResult.Failed, await Monitors.StopAsync("sub_1", Token));
            await Monitors.PendingSaved;
        }

        Assert.NotNull(_h.Find("dl_1"));
        Assert.Empty(_h.Silo.SentTo(HttpMethod.Delete, Monitor1Path));
        Assert.Equal(kept, File.ReadAllBytes(PendingPath));
    }

    [Fact]
    public async Task AFileOfWritesStillToSendThatCouldNotBeReadAtTheStart_IsReadWithALaterRun()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        _monitors = NewMonitors(PendingFile());
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.NetworkError());
        await Monitors.StopAsync("sub_1", Token);
        await Monitors.PendingSaved;
        Monitors.Dispose();

        // Granary starts again while another program holds the file.
        _monitors = NewMonitors(PendingFile());
        using (new FileStream(PendingPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Monitors.SyncAsync(Token);
            await Monitors.PendingSaved;
        }

        // The next run reads it: the stop goes under the validator it was made with.
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.NoContent());
        ListMonitors();
        await Monitors.SyncAsync(Token);
        await Monitors.PendingSaved;

        Assert.Equal("\"m1\"", _h.Silo.SentTo(HttpMethod.Delete, Monitor1Path)[^1].IfMatch);
        Assert.Empty(Monitors.Monitors);
        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public async Task AFileOfWritesStillToSendThatCannotEvenBeLookedAt_IsNotTakenForNone()
    {
        // In a folder of its own: this user may not list it while the file is denied.
        string path = Path.Combine(_h.Directory, "pending", "monitor-pending.json");
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        _monitors = NewMonitors(PendingFile(path));
        await Monitors.SyncAsync(Token);
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.NetworkError());
        await Monitors.StopAsync("sub_1", Token);
        await Monitors.PendingSaved;
        Monitors.Dispose();

        // Granary starts again while this user may not even look at the file, so Windows says it is not there.
        _monitors = NewMonitors(PendingFile(path));
        using (new DeniedRead(path))
        {
            await Monitors.SyncAsync(Token);
            await Monitors.PendingSaved;
        }

        // The stop it holds is not lost: the monitor does not come back, and nothing ran meanwhile.
        Assert.Empty(Monitors.Monitors);
        Assert.Single(_h.Silo.SentTo(HttpMethod.Post, SyncPath));

        // Once it can be read, the stop goes under the validator it was made with.
        _h.Silo.On(HttpMethod.Delete, Monitor1Path, ScriptedSilo.NoContent());
        ListMonitors();
        await Monitors.SyncAsync(Token);
        await Monitors.PendingSaved;

        Assert.Equal("\"m1\"", _h.Silo.SentTo(HttpMethod.Delete, Monitor1Path)[^1].IfMatch);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Runs_ComeAtTheStart_OnAProfileSwitch_AndEveryFourHours()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());

        Monitors.Start();
        _h.Time.Advance(SeriesMonitors.Settle);
        await Until(() => _h.Silo.SentTo(HttpMethod.Post, SyncPath).Count == 1, "the start's run");
        await Monitors.LatestRun;

        _h.SwitchProfile("8");
        _h.Time.Advance(SeriesMonitors.Settle);
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, MonitorsPath).Count == 2, "the switch's run");
        await Monitors.LatestRun;
        _h.SwitchProfile("7");
        _h.Time.Advance(SeriesMonitors.Settle);
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, MonitorsPath).Count == 3, "the switch back's run");
        await Monitors.LatestRun;

        _h.Time.Advance(SeriesMonitors.SyncInterval);
        _h.Time.Advance(SeriesMonitors.Settle);
        await Until(() => _h.Silo.SentTo(HttpMethod.Get, MonitorsPath).Count == 4, "the four hours' run");
    }

    [Fact]
    public async Task SigningOut_LeavesTheMonitorsOnTheServer()
    {
        ListMonitors(MonitorPayloads.Monitor());
        SyncAnswers(MonitorPayloads.Sync());
        Monitors.Start();
        _h.Time.Advance(SeriesMonitors.Settle);
        await Until(() => _h.Silo.SentTo(HttpMethod.Post, SyncPath).Count == 1, "the start's run");
        await Monitors.LatestRun;

        _h.SignOut();
        _h.Time.Advance(SeriesMonitors.Settle);
        await Monitors.LatestRun;

        Assert.Empty(_h.Silo.SentTo(HttpMethod.Delete, Monitor1Path));
        Assert.Empty(Monitors.Monitors);
        Assert.Single(_book.In(Scope7));
    }
}
