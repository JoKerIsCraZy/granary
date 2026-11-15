using System.Net;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Core.Servers;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The positions recorded offline go to the server as soon as Granary hears from it again (<see cref="ServerRecovery"/>):
/// Retry finding the server, the capability read after a read that failed, the events connection back (RealtimeEventsTests),
/// each once the settle has passed and once for a recovery that several of them tell, never while nothing waits. The
/// five-minute retry stays for a server that is away again.
/// </summary>
public sealed class OfflineProgressRecoveryTests : IAsyncDisposable
{
    private const string SyncPath = "api/v2/sync/progress";
    private const string ProgressPath = "api/v2/progress";

    private readonly DownloadHarness _h = new();
    private readonly OfflineProgressQueue _queue;
    private readonly ServerRecovery _recovery;
    private readonly OfflineProgressSync _sync;
    private int _recoveries;

    public OfflineProgressRecoveryTests()
    {
        _queue = new OfflineProgressQueue(Path.Combine(_h.Directory, "data", "offline-progress.json"), _h.Time, _h.Logs.For<OfflineProgressQueue>());
        _recovery = new ServerRecovery(_h.Logs.For<ServerRecovery>());
        _recovery.Recovered += (_, _) => Interlocked.Increment(ref _recoveries);
        _sync = new OfflineProgressSync(_queue, new ProgressSyncApi(_h.Silo.Http), _h.Store, _h.Session, _h.Network,
            new ViewerStateChanges(new PlaybackStops()), _h.Time, _h.Logs.For<OfflineProgressSync>(), _recovery);
        _h.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [], "page": { "has_more": false } }"""));
    }

    public async ValueTask DisposeAsync()
    {
        _sync.Dispose();
        await _sync.LatestRun;
        await _queue.LatestSave;
        await _h.DisposeAsync();
    }

    private IReadOnlyList<SentRequest> Sends => _h.Silo.SentTo(HttpMethod.Post, SyncPath);

    private int Recoveries => Volatile.Read(ref _recoveries);

    private static Func<Task<HttpResponseMessage>> Answered(string id) => ScriptedSilo.Json(HttpStatusCode.OK,
        $$"""{ "items": [ { "index": 0, "media_item_id": "{{id}}", "status": "success" } ], "summary": { "total": 1, "succeeded": 1, "failed": 0 } }""");

    // A position recorded offline, and the sync started while the server cannot be reached: its start's send fails, and the
    // position waits for the five-minute retry. Then the server is back, and takes what it is sent.
    private async Task StartWhileTheServerIsAwayAsync()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 7_200_000);
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.NetworkError(), Answered("ep_5"));
        _sync.Start();
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Sends.Count == 1, "the start's send");
        await _sync.LatestRun;
        Assert.Equal(OfflineProgressState.Pending, Assert.Single(_queue.Entries(Scope7)).State);
    }

    // The settle passes, and the run the recovery asked for has sent what waited.
    private async Task SentAfterTheSettleAsync()
    {
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Sends.Count == 2, "the recovery's send");
        await _sync.LatestRun;
        Assert.Empty(_queue.Entries(Scope7));
    }

    private void ServerAway() => _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.NetworkError());

    private void ServerBack() => _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, DownloadPayloads.Capability()));

    [Fact]
    public async Task ARetryThatFindsTheServer_SendsWhatWaits_OnceTheSettlePassed()
    {
        await _h.AddAsync("dl_5", DownloadState.Completed, mediaItemId: "ep_5");
        var start = new OfflineStart(_h.Store, _h.Api, _h.Session, _h.Time, _h.Logs.For<OfflineStart>(), _recovery);
        ServerAway();
        Assert.True(await start.CheckAtStartAsync(Token));
        await StartWhileTheServerIsAwayAsync();
        ServerBack();

        Assert.True(await start.RetryAsync(Token));
        Assert.Equal(1, Recoveries);
        // The settle gathers what else the recovery brings; nothing goes before it.
        _h.Time.Advance(OfflineProgressSync.Settle - TimeSpan.FromMilliseconds(1));
        Assert.Single(Sends);

        _h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => Sends.Count == 2, "the recovery's send");
        await _sync.LatestRun;
        Assert.Empty(_queue.Entries(Scope7));
    }

    [Fact]
    public async Task ARecoveryWhileThePositionIsOnItsWay_SendsItAgainAfterThatSend_NotAtTheRetry()
    {
        // The only position is on its way when the server answers again; that send then fails, and the position waits.
        _queue.Record(Scope7, "ep_5", 10_000, 7_200_000);
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Post, SyncPath, () => answer.Task, Answered("ep_5"));
        _sync.Start();
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Sends.Count == 1, "the start's send left");
        Assert.Equal(OfflineProgressState.Sending, Assert.Single(_queue.Entries(Scope7)).State);

        _recovery.Report("the test");
        _h.Time.Advance(OfflineProgressSync.Settle);
        answer.SetException(new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

        // The run the recovery asked for waited for that send, and sends the position again: not five minutes on.
        await Until(() => Sends.Count == 2, "the recovery's send");
        await _sync.LatestRun;
        Assert.Empty(_queue.Entries(Scope7));
    }

    [Fact]
    public async Task ARetryThatStillCannotReachTheServer_IsNoRecovery()
    {
        await _h.AddAsync("dl_5", DownloadState.Completed, mediaItemId: "ep_5");
        var start = new OfflineStart(_h.Store, _h.Api, _h.Session, _h.Time, _h.Logs.For<OfflineStart>(), _recovery);
        ServerAway();
        Assert.True(await start.CheckAtStartAsync(Token));

        Assert.False(await start.RetryAsync(Token));

        Assert.Equal(0, Recoveries);
    }

    [Fact]
    public async Task TheCapabilityReadAfterAReadThatFailed_SendsWhatWaits()
    {
        var feature = new DownloadsFeature(_h.Session, _h.Api, _h.Store, _h.Time, _h.Logs.For<DownloadsFeature>(), _recovery);
        ServerAway();
        await feature.RefreshAsync(Token);
        await StartWhileTheServerIsAwayAsync();
        ServerBack();
        // A failed read is tried again a minute on, at the next page change.
        _h.Time.Advance(DownloadsFeature.RetryAfter);

        await feature.RefreshAsync(Token);

        Assert.True(feature.CanDownload);
        Assert.Equal(1, Recoveries);
        await SentAfterTheSettleAsync();
    }

    [Fact]
    public async Task AFirstCapabilityRead_AndOneThatFails_AreNoRecovery()
    {
        var answered = new DownloadsFeature(_h.Session, _h.Api, _h.Store, _h.Time, _h.Logs.For<DownloadsFeature>(), _recovery);
        await answered.RefreshAsync(Token);
        ServerAway();
        var away = new DownloadsFeature(_h.Session, _h.Api, _h.Store, _h.Time, _h.Logs.For<DownloadsFeature>(), _recovery);
        await away.RefreshAsync(Token);
        _h.Time.Advance(DownloadsFeature.RetryAfter);
        await away.RefreshAsync(Token);

        Assert.Equal(0, Recoveries);
    }

    [Fact]
    public async Task ARecovery_SendsNothing_WhileNothingWaits()
    {
        // A download on this PC: a run would read the server's progress back for it.
        await _h.AddAsync("dl_5", DownloadState.Completed, mediaItemId: "ep_5");
        _sync.Start();
        _h.Time.Advance(OfflineProgressSync.Settle);
        await _sync.LatestRun;
        var lastRun = _sync.LatestRun;
        int sent = _h.Silo.Sent.Count;

        _recovery.Report("a test");
        _h.Time.Advance(OfflineProgressSync.Settle);

        Assert.Same(lastRun, _sync.LatestRun);
        Assert.Equal(sent, _h.Silo.Sent.Count);
    }

    [Fact]
    public async Task RecoveriesTogether_SendOnce()
    {
        await StartWhileTheServerIsAwayAsync();

        // Retry finds the server, the events connection comes back a moment later, and the capability is read again.
        _recovery.Report("Retry");
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        _recovery.Report("the events connection");
        _recovery.Report("the downloads capability");
        await SentAfterTheSettleAsync();
        _h.Time.Advance(OfflineProgressSync.Settle);

        Assert.Equal(2, Sends.Count);
    }

    [Fact]
    public async Task TheFiveMinuteRetry_StaysForAServerThatIsAwayAgain()
    {
        await StartWhileTheServerIsAwayAsync();
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.NetworkError(), Answered("ep_5"));

        _recovery.Report("the events connection");
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Sends.Count == 2, "the recovery's send");
        await _sync.LatestRun;
        Assert.Single(_queue.Entries(Scope7));

        _h.Time.Advance(OfflineProgressSync.RetryInterval);
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Sends.Count == 3, "the retry's send");
        await _sync.LatestRun;
        Assert.Empty(_queue.Entries(Scope7));
    }
}
