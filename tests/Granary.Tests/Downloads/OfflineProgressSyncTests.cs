using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// What was watched from this PC goes to the server once it can be reached, and the server's progress comes back: each
/// answer's effect on the queue, and which of two positions holds.
/// </summary>
public sealed class OfflineProgressSyncTests : IAsyncDisposable
{
    private const string SyncPath = "api/v2/sync/progress";
    private const string ProgressPath = "api/v2/progress";

    private readonly DownloadHarness _h = new();
    private readonly OfflineProgressQueue _queue;
    private readonly List<OfflineProgressSync> _syncs = [];
    private readonly PlaybackStops _stops = new();
    private readonly ViewerStateChanges _changes;
    private int _told;

    public OfflineProgressSyncTests()
    {
        _queue = new OfflineProgressQueue(Path.Combine(_h.Directory, "data", "offline-progress.json"), _h.Time, _h.Logs.For<OfflineProgressQueue>());
        _changes = new ViewerStateChanges(_stops);
        _changes.Changed += (_, _) => Interlocked.Increment(ref _told);
        _h.Silo.On(HttpMethod.Post, SyncPath, Answered("ep_5"));
        _h.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, Listing(null)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sync in _syncs)
        {
            sync.Dispose();
            await sync.LatestRun;
        }
        await _queue.LatestSave;
        await _h.DisposeAsync();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private OfflineProgressSync Sync()
    {
        var sync = new OfflineProgressSync(_queue, new ProgressSyncApi(_h.Silo.Http), _h.Store, _h.Session, _h.Network, _changes, _h.Time,
            _h.Logs.For<OfflineProgressSync>());
        _syncs.Add(sync);
        return sync;
    }

    private static Func<Task<HttpResponseMessage>> Answered(params string[] items) => ScriptedSilo.Json(HttpStatusCode.OK,
        "{ \"items\": [" + string.Join(",", items.Select((id, index) => $$"""{ "index": {{index}}, "media_item_id": "{{id}}", "status": "success" }"""))
        + $$"""], "summary": { "total": {{items.Length}}, "succeeded": {{items.Length}}, "failed": 0 } }""");

    private static string Row(string id, double seconds, string updatedAt, bool completed = false) =>
        $$"""{ "media_item_id": "{{id}}", "position_seconds": {{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "duration_seconds": 7200, "completed": {{(completed ? "true" : "false")}}, "updated_at": "{{updatedAt}}" }""";

    private static string Listing(string? nextCursor, params string[] rows) =>
        "{ \"items\": [" + string.Join(",", rows) + "], \"page\": " +
        (nextCursor is null ? """{ "has_more": false }""" : $$"""{ "has_more": true, "next_cursor": "{{nextCursor}}" }""") + " }";

    private IReadOnlyList<SentRequest> Sends => _h.Silo.SentTo(HttpMethod.Post, SyncPath);

    private IReadOnlyList<SentRequest> Reads => _h.Silo.SentTo(HttpMethod.Get, ProgressPath);

    [Fact]
    public async Task Flush_SendsMilliseconds_AndTheTimeItWasRecorded_AsTheProfileItWasRecordedAs()
    {
        _queue.Record(Scope7, "ep_5", 1_830_500, 7_200_000);
        _h.Time.Advance(TimeSpan.FromHours(2));

        await Sync().FlushAsync(readBack: false, Token);

        var sent = Assert.Single(Sends);
        Assert.Equal("""{"items":[{"media_item_id":"ep_5","position_ms":1830500,"duration_ms":7200000,"updated_at":"2026-10-01T12:00:00+00:00"}]}""", sent.Body);
        Assert.Equal(("1", "7"), (sent.AsAccount, sent.AsProfile));
        Assert.Empty(_queue.Entries(Scope7));
        // A send alone asks for nothing back.
        Assert.Empty(Reads);
    }

    [Fact]
    public async Task AnItemTheServerTook_AndOneItWillNeverTake_LeaveTheQueue()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _queue.Record(Scope7, "ep_gone", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Json(HttpStatusCode.OK, """
            { "items": [ { "index": 0, "media_item_id": "ep_5", "status": "success" },
                         { "index": 1, "media_item_id": "ep_gone", "status": "failure", "failure": { "type": "x", "title": "Not found", "status": 404 } } ],
              "summary": { "total": 2, "succeeded": 1, "failed": 1 } }
            """));

        await Sync().FlushAsync(readBack: false, Token);

        // A title the profile can no longer see will not be taken at a second try either.
        Assert.Empty(_queue.Entries(Scope7));
        Assert.Single(Sends);
    }

    [Fact]
    public async Task AnItemTheServerFailedOn_IsSentAgain_WhileTheOthersOfItsBatchLeave()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _queue.Record(Scope7, "ep_6", 20_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Json(HttpStatusCode.OK, """
            { "items": [ { "index": 0, "media_item_id": "ep_5", "status": "success" },
                         { "index": 1, "media_item_id": "ep_6", "status": "failure", "failure": { "type": "x", "title": "Internal error", "status": 500 } } ],
              "summary": { "total": 2, "succeeded": 1, "failed": 1 } }
            """), Answered("ep_6"));
        var sync = Sync();

        await sync.FlushAsync(readBack: false, Token);

        // The server's store failed on it: nothing says it would fail again. It is not claimed again within the run.
        Assert.Equal([("ep_6", OfflineProgressState.Pending)], _queue.Entries(Scope7).Select(e => (e.MediaItemId, e.State)));
        Assert.Single(Sends);

        _h.Time.Advance(OfflineProgressSync.RetryInterval);
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => _queue.Entries(Scope7).Count == 0, "the item went with the retry");
        Assert.Contains("\"position_ms\":20000", Sends[1].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(408)]
    [InlineData(409)]
    [InlineData(412)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task AServerThatRefusesOrFailsForNow_KeepsTheEntriesForLater(int status)
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Problem(status, "problem"), Answered("ep_5"));
        var sync = Sync();

        await sync.FlushAsync(readBack: false, Token);
        Assert.Equal(OfflineProgressState.Pending, Assert.Single(_queue.Entries(Scope7)).State);

        // They go with the next try, five minutes on.
        _h.Time.Advance(OfflineProgressSync.RetryInterval);
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => _queue.Entries(Scope7).Count == 0, "the entry went with the retry");
        Assert.Equal(2, Sends.Count);
    }

    [Fact]
    public async Task ARequestThatNeverLeft_StaysPending()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.NetworkError());

        await Sync().FlushAsync(readBack: true, Token);

        Assert.Equal(OfflineProgressState.Pending, Assert.Single(_queue.Entries(Scope7)).State);
        // Without the server there is nothing to read back either.
        Assert.Empty(Reads);
    }

    [Theory]
    [InlineData("ended")]
    [InlineData("timeout")]
    [InlineData("malformed")]
    [InlineData("short")]
    [InlineData("others")]
    public async Task ASendWithNoUsableAnswer_IsSentAgain(string answer)
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, answer switch
        {
            "ended" => () => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "the connection broke off")),
            // HttpClient's own timeout: a cancellation nobody asked for.
            "timeout" => () => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out", new TimeoutException())),
            "malformed" => ScriptedSilo.Json(HttpStatusCode.OK, "{ not json"),
            // An answer for fewer items than were sent, or for other items, says nothing sure about any of them.
            "short" => ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [] }"""),
            _ => Answered("ep_other"),
        }, Answered("ep_5"));
        var sync = Sync();

        await sync.FlushAsync(readBack: false, Token);

        // The server may have taken it or not: the position is kept, and a second send of it changes nothing there.
        Assert.Equal(OfflineProgressState.Pending, Assert.Single(_queue.Entries(Scope7)).State);

        await sync.FlushAsync(readBack: false, Token);

        Assert.Empty(_queue.Entries(Scope7));
        Assert.Equal(2, Sends.Count);
        Assert.Equal(Sends[0].Body, Sends[1].Body);
    }

    [Fact]
    public async Task ASendThatEndsInSomethingUnexpected_KeepsItsEntries_AndTheNextRunSendsThem()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, () => throw new InvalidOperationException("boom"), Answered("ep_5"));
        var sync = Sync();

        await sync.FlushAsync(readBack: false, Token);

        // Not left on its way for the rest of the run.
        Assert.Equal(OfflineProgressState.Pending, Assert.Single(_queue.Entries(Scope7)).State);
        Assert.Contains("failed unexpectedly", _h.Logs.All, StringComparison.Ordinal);

        await sync.FlushAsync(readBack: false, Token);

        Assert.Empty(_queue.Entries(Scope7));
    }

    [Fact]
    public async Task APositionTheServerTookWhileItsAnswerGotLost_LeavesOnceTheServersProgressShowsIt()
    {
        await _h.AddPlayableAsync();
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath,
            () => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "the connection broke off")));
        // The server has the position, at the very time this PC recorded it.
        _h.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, Listing(null, Row("ep_5", 10, "2026-10-01T12:00:00.000Z"))));
        var sync = Sync();

        await sync.FlushAsync(readBack: true, Token);

        Assert.Empty(_queue.Entries(Scope7));
        Assert.Contains("already had 1", _h.Logs.All, StringComparison.Ordinal);

        await sync.FlushAsync(readBack: false, Token);
        Assert.Single(Sends);
    }

    [Fact]
    public async Task APositionWhoseSendGotNoAnswer_AndThatTheServerDoesNotShow_IsNotLost()
    {
        await _h.AddPlayableAsync(change: r => r with { ResumeMs = 10_000, ResumeAt = ManualTimeProvider.DefaultStart });
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath,
            () => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "the connection broke off")),
            Answered("ep_5"));
        // The server still has an older position: the request never got to it.
        _h.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, Listing(null, Row("ep_5", 3, "2026-10-01T11:00:00Z"))));
        var sync = Sync();

        await sync.FlushAsync(readBack: true, Token);

        // The read of the server's progress lets nothing go that it does not show.
        Assert.Equal(OfflineProgressState.Pending, Assert.Single(_queue.Entries(Scope7)).State);
        Assert.Equal(10_000, _h.Find("dl_1")!.Record.ResumeMs);

        await sync.FlushAsync(readBack: false, Token);

        Assert.Empty(_queue.Entries(Scope7));
        Assert.Equal(2, Sends.Count);
    }

    [Fact]
    public async Task APositionOnItsWayWhenGranaryClosed_IsSentAfterTheNextStart()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _queue.Claim(Scope7, OfflineProgressSync.MaxBatch);
        await _queue.LatestSave;
        var restarted = new OfflineProgressQueue(Path.Combine(_h.Directory, "data", "offline-progress.json"), _h.Time, _h.Logs.For<OfflineProgressQueue>());
        var sync = new OfflineProgressSync(restarted, new ProgressSyncApi(_h.Silo.Http), _h.Store, _h.Session, _h.Network, _changes, _h.Time,
            _h.Logs.For<OfflineProgressSync>());
        _syncs.Add(sync);

        await sync.FlushAsync(readBack: false, Token);
        await restarted.LatestSave;

        Assert.Single(Sends);
        Assert.Empty(restarted.Entries(Scope7));
    }

    [Fact]
    public async Task WishesMadeWhileASendHangs_MakeOneMoreRun_NotOneEach()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Post, SyncPath, () => answer.Task, Answered("ep_5"));
        var sync = Sync();
        var first = sync.FlushAsync(readBack: false, Token);
        await Until(() => Sends.Count == 1, "the first send left");

        // The player asks every ten seconds while the server takes the request and does not answer.
        var second = sync.FlushAsync(readBack: false, Token);
        var third = sync.FlushAsync(readBack: false, Token);
        var waiting = sync.LatestRun;
        _ = sync.FlushAsync(readBack: false, Token);

        Assert.Same(waiting, sync.LatestRun);
        answer.SetException(new HttpRequestException(HttpRequestError.ResponseEnded, "the connection broke off"));
        await Task.WhenAll(first, second, third);

        // One run waited, and sent what the first could not.
        Assert.Equal(2, Sends.Count);
        Assert.Empty(_queue.Entries(Scope7));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(422)]
    public async Task ARequestTheServerWillNeverTake_LeavesTheQueue(int status)
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Problem(status, "problem"));

        await Sync().FlushAsync(readBack: false, Token);

        Assert.Empty(_queue.Entries(Scope7));
        Assert.Contains("refused", _h.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnceTheServerTookAPosition_ThePagesAreTold_Once()
    {
        await _h.AddPlayableAsync();
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _queue.Record(Scope7, "mv_1", 20_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, Answered("ep_5", "mv_1"));
        var sync = Sync();

        await sync.FlushAsync(readBack: true, Token);

        // Continue Watching, Next Up and the title's page show where the viewer left off, as after a stream.
        Assert.Equal(1, Volatile.Read(ref _told));

        // A run with nothing to send says nothing.
        await sync.FlushAsync(readBack: true, Token);
        Assert.Equal(1, Volatile.Read(ref _told));
    }

    [Fact]
    public async Task APositionSentLater_OnceTheServerIsBack_TellsThePagesThen()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.NetworkError(), ScriptedSilo.Problem(503, "unavailable"), Answered("ep_5"));
        var sync = Sync();
        sync.Start();
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Sends.Count == 1, "the start tried");
        await sync.LatestRun;
        _h.Network.SetMetered(false);
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Sends.Count == 2, "the link's change tried");
        await sync.LatestRun;

        // Nothing reached the server yet: the pages would only read what they have.
        Assert.Equal(0, Volatile.Read(ref _told));

        _h.Time.Advance(OfflineProgressSync.RetryInterval);
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Volatile.Read(ref _told) == 1, "the pages were told once the position arrived");
        Assert.Empty(_queue.Entries(Scope7));
    }

    [Fact]
    public async Task WhileATitlePlays_ASendTellsNobody_SinceItsStopWill()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        var sync = Sync();

        using (_stops.Playing())
        {
            await sync.FlushAsync(readBack: false, Token);
        }

        // Home under a minimized player is not loaded again with every position the play records.
        Assert.Single(Sends);
        Assert.Equal(0, Volatile.Read(ref _told));
    }

    [Fact]
    public async Task APositionTheServerTookWithoutAnAnswer_TellsThePages_OnceTheReadShowsIt()
    {
        await _h.AddPlayableAsync();
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath,
            () => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "the connection broke off")));
        _h.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, Listing(null, Row("ep_5", 10, "2026-10-01T12:00:00.000Z"))));

        await Sync().FlushAsync(readBack: true, Token);

        Assert.Equal(1, Volatile.Read(ref _told));
    }

    // The server as it validates a batch: one item it cannot take fails the whole request (422); else each item succeeds.
    private void ServerRefusesRequestsWith(params string[] bad) => _h.Silo.On(HttpMethod.Post, SyncPath, () =>
    {
        string body = Sends[^1].Body!;
        var ids = System.Text.RegularExpressions.Regex.Matches(body, "\"media_item_id\":\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        return ids.Any(bad.Contains) ? ScriptedSilo.Problem(422, "validation_failed")() : Answered(ids)();
    });

    [Theory]
    [InlineData(5, 7)]
    [InlineData(100, 15)]
    public async Task OneEntryTheServerRefuses_IsDroppedAlone_AndTheOthersOfItsBatchArrive(int count, int mostRequests)
    {
        foreach (int n in Enumerable.Range(0, count))
        {
            _queue.Record(Scope7, "ep_" + n, 10_000 + n, 0);
        }
        ServerRefusesRequestsWith("ep_3");

        await Sync().FlushAsync(readBack: false, Token);

        // The batch was refused as a whole, then sent again in halves until the one entry stood alone.
        Assert.Empty(_queue.Entries(Scope7));
        var arrived = Sends.Where(s => !s.Body!.Contains("\"ep_3\"", StringComparison.Ordinal))
            .SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s.Body!, "\"media_item_id\":\"([^\"]+)\"").Select(m => m.Groups[1].Value)).ToList();
        Assert.Equal(count - 1, arrived.Distinct().Count());
        Assert.Equal(arrived.Count, arrived.Distinct().Count());
        Assert.InRange(Sends.Count, 2, mostRequests);
        Assert.Contains("refused 1 position(s) for good", _h.Logs.All, StringComparison.Ordinal);
        // The pages hear that the others arrived.
        Assert.Equal(1, Volatile.Read(ref _told));
    }

    [Fact]
    public async Task ManyEntriesTheServerRefuses_CostABoundedNumberOfRequestsARun_AndNoneThatCanGoIsLost()
    {
        foreach (int n in Enumerable.Range(0, 100))
        {
            _queue.Record(Scope7, "ep_" + n, 10_000 + n, 0);
        }
        // Every odd one is refused: halving finds them one at a time.
        ServerRefusesRequestsWith([.. Enumerable.Range(0, 100).Where(n => n % 2 == 1).Select(n => "ep_" + n)]);
        var sync = Sync();

        await sync.FlushAsync(readBack: false, Token);

        // A run stops halving after its share of requests; what it did not get to waits, and nothing is dropped unjudged.
        Assert.InRange(Sends.Count, 2, 1 + OfflineProgressSync.MaxSplitSends);
        var left = _queue.Entries(Scope7);
        Assert.NotEmpty(left);
        Assert.All(left, e => Assert.Equal(OfflineProgressState.Pending, e.State));

        // The next runs go on from there, each with fewer; in the end the even ones all arrived and the odd ones are gone.
        for (int run = 0; run < 40 && _queue.Entries(Scope7).Count > 0; run++)
        {
            await sync.FlushAsync(readBack: false, Token);
        }
        Assert.Empty(_queue.Entries(Scope7));
        var arrived = Sends.SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s.Body!, "\"media_item_id\":\"ep_(\\d+)\"")
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray() is var ids && ids.All(n => n % 2 == 0)
            ? ids : []).Distinct().Count();
        Assert.Equal(50, arrived);
    }

    [Fact]
    public async Task AHalfThatDoesNotArrive_StopsTheHalving_AndKeepsWhatIsLeft()
    {
        foreach (int n in Enumerable.Range(0, 4))
        {
            _queue.Record(Scope7, "ep_" + n, 10_000 + n, 0);
        }
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Problem(422, "validation_failed"), Answered("ep_0", "ep_1"), ScriptedSilo.NetworkError());

        await Sync().FlushAsync(readBack: false, Token);

        // The first half arrived; the connection went before the second: it waits, it is not dropped.
        Assert.Equal(["ep_2", "ep_3"], _queue.Entries(Scope7).Select(e => e.MediaItemId));
        Assert.Equal(3, Sends.Count);
    }

    [Fact]
    public async Task AnotherProfilesEntries_WaitForThatProfile()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _queue.Record(Scope8, "ep_5", 99_000, 0);
        var sync = Sync();

        await sync.FlushAsync(readBack: false, Token);

        Assert.Single(Sends);
        Assert.Equal(99_000, Assert.Single(_queue.Entries(Scope8)).PositionMs);

        _h.SwitchProfile("8");
        await sync.FlushAsync(readBack: false, Token);

        Assert.Equal("8", Sends[1].AsProfile);
        Assert.Contains("99000", Sends[1].Body, StringComparison.Ordinal);
        Assert.Empty(_queue.Entries(Scope8));
    }

    [Fact]
    public async Task AProfileSwitchedToWhileTheSendWasMade_GetsNoneOfTheOthersPositions()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        // The server acts as profile 8 by the time the request goes out: it is refused unsent.
        _h.Silo.ActingProfile = () => "8";

        await Sync().FlushAsync(readBack: true, Token);

        Assert.Empty(_h.Silo.Sent);
        Assert.Equal(OfflineProgressState.Pending, Assert.Single(_queue.Entries(Scope7)).State);
    }

    [Fact]
    public async Task AnAccountSignedInWhileTheSendWasMade_GetsNoneOfTheOthersPositions()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        // Another account is signed in on the server by the time the request goes out, and its profile has the same id.
        _h.Silo.ActingAccount = () => "2";

        await Sync().FlushAsync(readBack: true, Token);

        Assert.Empty(_h.Silo.Sent);
        Assert.Equal(OfflineProgressState.Pending, Assert.Single(_queue.Entries(Scope7)).State);
    }

    [Fact]
    public async Task ARun_SendsNoFurtherBatch_OnceAnotherAccountIsTheActiveOne()
    {
        foreach (int n in Enumerable.Range(0, 130))
        {
            _queue.Record(Scope7, "ep_" + n, 10_000, 0);
        }
        var first = Answered([.. Enumerable.Range(0, 100).Select(n => "ep_" + n)]);
        _h.Silo.On(HttpMethod.Post, SyncPath, () =>
        {
            // While the first batch is answered, the viewer signs out and another account signs in; its profile is 7 too.
            _h.Session.Account = _h.Session.Account! with { Id = "2" };
            return first();
        });

        await Sync().FlushAsync(readBack: true, Token);

        // The thirty that were left are account 1's: they wait for it, and nothing is read as the other account.
        Assert.Single(Sends);
        Assert.Empty(Reads);
        Assert.Equal(30, _queue.Entries(Scope7).Count);
        Assert.All(_queue.Entries(Scope7), e => Assert.Equal(OfflineProgressState.Pending, e.State));
    }

    [Fact]
    public async Task ARead_FollowsNoFurtherPage_AndTakesNothing_OnceAnotherServerIsTheActiveOne()
    {
        await _h.AddPlayableAsync();
        _h.Silo.OnUrl(HttpMethod.Get, ProgressPath, (_, _) =>
        {
            _h.Session.ActiveServer = ScriptedSilo.Entry("srv2", new Uri("https://other.example/silo"));
            return ScriptedSilo.Json(HttpStatusCode.OK, Listing("c2", Row("ep_5", 600, "2026-10-01T13:30:00Z")))();
        });

        await Sync().FlushAsync(readBack: true, Token);

        Assert.Single(Reads);
        Assert.Null(_h.Find("dl_1")!.Record.ResumeMs);
    }

    [Fact]
    public async Task MoreThanAHundredEntries_GoInBatches()
    {
        foreach (int n in Enumerable.Range(0, 130))
        {
            _queue.Record(Scope7, "ep_" + n, 10_000, 0);
        }
        _h.Silo.On(HttpMethod.Post, SyncPath, Answered([.. Enumerable.Range(0, 100).Select(n => "ep_" + n)]),
            Answered([.. Enumerable.Range(100, 30).Select(n => "ep_" + n)]));

        await Sync().FlushAsync(readBack: false, Token);

        Assert.Equal(2, Sends.Count);
        Assert.Empty(_queue.Entries(Scope7));
    }

    [Fact]
    public async Task AnOlderOfflinePosition_DoesNotMoveTheResumePointBack()
    {
        // Watched to 20 min on this PC at 12:00, offline; on another device to 50 min at 13:00.
        await _h.AddPlayableAsync(change: r => r with { ResumeMs = 1_200_000, ResumeAt = ManualTimeProvider.DefaultStart });
        _queue.Record(Scope7, "ep_5", 1_200_000, 7_200_000);
        _h.Time.Advance(TimeSpan.FromHours(2));
        _h.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, Listing(null, Row("ep_5", 3000, "2026-10-01T13:00:00Z"))));

        await Sync().FlushAsync(readBack: true, Token);

        // The entry went out with the time it was recorded, so the server keeps its own newer one.
        Assert.Contains("\"updated_at\":\"2026-10-01T12:00:00+00:00\"", Assert.Single(Sends).Body, StringComparison.Ordinal);
        var record = _h.Find("dl_1")!.Record;
        Assert.Equal((3_000_000L, new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero)), (record.ResumeMs, record.ResumeAt));
    }

    [Fact]
    public async Task AServerPositionOlderThanOneThisPCStillHasToSend_LeavesThisPCsAlone()
    {
        _h.Time.Advance(TimeSpan.FromHours(1));
        await _h.AddPlayableAsync(change: r => r with { ResumeMs = 3_000_000, ResumeAt = ManualTimeProvider.DefaultStart.AddHours(1) });
        _queue.Record(Scope7, "ep_5", 3_000_000, 7_200_000);
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.NetworkError());
        _h.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, Listing(null, Row("ep_5", 1200, "2026-10-01T12:00:00Z"))));

        // The send fails; the server's progress is read all the same.
        await Sync().FlushAsync(readBack: true, Token);

        Assert.Single(Reads);
        var record = _h.Find("dl_1")!.Record;
        Assert.Equal((3_000_000L, ManualTimeProvider.DefaultStart.AddHours(1)), (record.ResumeMs, record.ResumeAt));
        Assert.Single(_queue.Entries(Scope7));
    }

    [Fact]
    public async Task APositionTheServerAnsweredForButDidNotKeep_GivesWayToWhatTheServerHas()
    {
        // A watched film, opened again for a minute: too short for the server to keep, so its row stays as it was.
        _h.Time.Advance(TimeSpan.FromHours(1));
        await _h.AddPlayableAsync(change: r => r with { ResumeMs = 60_000, ResumeAt = ManualTimeProvider.DefaultStart.AddHours(1), IsWatched = false });
        _queue.Record(Scope7, "ep_5", 60_000, 7_200_000);
        _h.Silo.On(HttpMethod.Get, ProgressPath,
            ScriptedSilo.Json(HttpStatusCode.OK, Listing(null, Row("ep_5", 0, "2026-10-01T09:00:00Z", completed: true))));

        await Sync().FlushAsync(readBack: true, Token);

        // The server answered for the position: what it has now is what became of it. The film is still watched.
        var record = _h.Find("dl_1")!.Record;
        Assert.Equal((0L, true), (record.ResumeMs, record.IsWatched));
        Assert.Empty(_queue.Entries(Scope7));
    }

    [Fact]
    public async Task AServerWithAHigherWatchedThreshold_SaysTheTitleIsNotWatched_AndWhereItResumes()
    {
        // Left at 92 %: this PC lets it start over, and the server, set to 95 %, keeps the position and no watched mark.
        await _h.AddPlayableAsync(change: r => r with { ResumeMs = 0, ResumeAt = ManualTimeProvider.DefaultStart });
        _queue.Record(Scope7, "ep_5", 6_624_000, 7_200_000);
        _h.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, Listing(null, Row("ep_5", 6624, "2026-10-01T12:00:00.000Z"))));

        await Sync().FlushAsync(readBack: true, Token);

        var record = _h.Find("dl_1")!.Record;
        Assert.Equal((6_624_000L, false), (record.ResumeMs, record.IsWatched));
    }

    [Fact]
    public async Task ATitleBeingWatchedAgain_IsNotWatched_UntilThatWatchEnds()
    {
        await _h.AddPlayableAsync(change: r => r with { IsWatched = true });
        // The server keeps "completed" from the first watch, with the new watch's position beside it.
        _h.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK,
            Listing(null, Row("ep_5", 1500, "2026-10-01T11:00:00Z", completed: true))),
            ScriptedSilo.Json(HttpStatusCode.OK, Listing(null, Row("ep_5", 0, "2026-10-01T12:30:00Z", completed: true))));
        var sync = Sync();

        await sync.FlushAsync(readBack: true, Token);
        Assert.Equal((1_500_000L, false), (_h.Find("dl_1")!.Record.ResumeMs, _h.Find("dl_1")!.Record.IsWatched));

        _h.Time.Advance(OfflineProgressSync.ReadBackInterval);
        await sync.FlushAsync(readBack: true, Token);
        Assert.Equal((0L, true), (_h.Find("dl_1")!.Record.ResumeMs, _h.Find("dl_1")!.Record.IsWatched));
    }

    [Fact]
    public async Task TheServersAnswerToThisPCsOwnPosition_Holds_AlsoAtTheSameInstant()
    {
        // This PC recorded the end of the episode; the server counts it as watched and sets its position back to 0, at the
        // very time this PC named.
        await _h.AddPlayableAsync(change: r => r with { ResumeMs = 7_190_000, ResumeAt = ManualTimeProvider.DefaultStart });
        _h.Silo.On(HttpMethod.Get, ProgressPath,
            ScriptedSilo.Json(HttpStatusCode.OK, Listing(null, Row("ep_5", 0, "2026-10-01T12:00:00.000Z", completed: true))));

        await Sync().FlushAsync(readBack: true, Token);

        var record = _h.Find("dl_1")!.Record;
        Assert.Equal((0L, true), (record.ResumeMs, record.IsWatched));
    }

    [Fact]
    public async Task AReadBackThatSaysWhatTheDownloadsAlreadyHave_ChangesNothing()
    {
        await _h.AddPlayableAsync("dl_5", "ep_5", change: r => r with
        {
            ResumeMs = 600_000, ResumeAt = new DateTimeOffset(2026, 10, 1, 11, 30, 0, TimeSpan.Zero),
        });
        await _h.AddPlayableAsync("dl_7", "ep_7", episode: 7);
        _h.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK,
            Listing(null, Row("ep_5", 600, "2026-10-01T11:30:00Z"), Row("ep_7", 300, "2026-10-01T11:00:00Z"))));
        var sync = Sync();
        int changes = 0;
        _h.Store.Changed += (_, _) => Interlocked.Increment(ref changes);

        await sync.FlushAsync(readBack: true, Token);

        // Only the download whose position is new is written, and only it is told to the pages.
        Assert.Equal(1, changes);
        Assert.Equal(300_000, _h.Find("dl_7")!.Record.ResumeMs);
    }

    [Fact]
    public async Task TheReadBack_FollowsEveryPage_AndMarksWatched()
    {
        await _h.AddPlayableAsync("dl_5", "ep_5");
        await _h.AddPlayableAsync("dl_7", "ep_7", episode: 7);
        _h.Silo.OnUrl(HttpMethod.Get, ProgressPath, (url, _) => ScriptedSilo.Json(HttpStatusCode.OK, url.Query.Contains("cursor=c2", StringComparison.Ordinal)
            ? Listing(null, Row("ep_7", 0, "2026-10-01T11:00:00Z", completed: true))
            : Listing("c2", Row("ep_5", 600, "2026-10-01T11:30:00Z"), Row("mv_not_here", 50, "2026-10-01T11:40:00Z")))());

        await Sync().FlushAsync(readBack: true, Token);

        Assert.Equal(2, Reads.Count);
        Assert.Contains("limit=200", Reads[0].Uri.Query, StringComparison.Ordinal);
        Assert.All(Reads, r => Assert.Equal(("1", "7"), (r.AsAccount, r.AsProfile)));
        Assert.Equal((600_000L, false), (_h.Find("dl_5")!.Record.ResumeMs, _h.Find("dl_5")!.Record.IsWatched));
        Assert.Equal((0L, true), (_h.Find("dl_7")!.Record.ResumeMs, _h.Find("dl_7")!.Record.IsWatched));
    }

    [Fact]
    public async Task AReadBackThatBreaksOff_ChangesNothing_AndKeepsWhatWaits()
    {
        await _h.AddPlayableAsync();
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath,
            () => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "the connection broke off")));
        _h.Silo.OnUrl(HttpMethod.Get, ProgressPath, (url, _) => url.Query.Contains("cursor=c2", StringComparison.Ordinal)
            ? ScriptedSilo.NetworkError()()
            : ScriptedSilo.Json(HttpStatusCode.OK, Listing("c2", Row("ep_5", 600, "2026-10-01T13:30:00Z")))());

        await Sync().FlushAsync(readBack: true, Token);

        Assert.Null(_h.Find("dl_1")!.Record.ResumeMs);
        Assert.Equal(OfflineProgressState.Pending, Assert.Single(_queue.Entries(Scope7)).State);
    }

    [Theory]
    [InlineData("""{ "items": [ ROW ] }""")]
    [InlineData("""{ "items": [ ROW ], "page": { "has_more": true } }""")]
    [InlineData("""{ "items": [ ROW ], "page": { "has_more": true, "next_cursor": "" } }""")]
    public async Task AListingThatDoesNotSayItIsComplete_ChangesNothing(string answer)
    {
        await _h.AddPlayableAsync();
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.NetworkError());
        // A row that would both move the download and settle the waiting position, were the listing whole.
        _h.Silo.On(HttpMethod.Get, ProgressPath,
            ScriptedSilo.Json(HttpStatusCode.OK, answer.Replace("ROW", Row("ep_5", 600, "2026-10-01T13:30:00Z"), StringComparison.Ordinal)));

        await Sync().FlushAsync(readBack: true, Token);

        // More rows may follow that it does not give a way to: nothing is taken, and nothing that waits is let go.
        Assert.Null(_h.Find("dl_1")!.Record.ResumeMs);
        Assert.Single(_queue.Entries(Scope7));
        Assert.Contains("did not end", _h.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStopWhosePositionWentWithTheLastRecord_StillLearnsWhetherTheTitleIsWatchedNow()
    {
        await _h.AddPlayableAsync();
        var sync = Sync();
        await sync.FlushAsync(readBack: true, Token);
        Assert.Single(Reads);

        // The play's last record goes while it plays, and asks for nothing back.
        _queue.Record(Scope7, "ep_5", 6_800_000, 7_200_000);
        await sync.FlushAsync(readBack: false, Token);
        Assert.Single(Reads);

        // The viewer pauses and stops there: the stop has nothing new to send, a minute after the last read. The server,
        // whose threshold an admin set, counts the title as watched.
        _h.Silo.On(HttpMethod.Get, ProgressPath,
            ScriptedSilo.Json(HttpStatusCode.OK, Listing(null, Row("ep_5", 0, "2026-10-01T12:00:00.000Z", completed: true))));
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await sync.FlushAsync(readBack: true, Token);

        Assert.Equal(2, Reads.Count);
        Assert.True(_h.Find("dl_1")!.Record.IsWatched);

        // Read once for it: the next stop without a send waits for the ten minutes again.
        await sync.FlushAsync(readBack: true, Token);
        Assert.Equal(2, Reads.Count);
    }

    [Fact]
    public async Task WithoutDownloads_NothingIsReadBack()
    {
        await Sync().FlushAsync(readBack: true, Token);

        Assert.Empty(_h.Silo.Sent);
    }

    [Fact]
    public async Task TheServersProgress_IsNotReadAgainWithinTenMinutes_UnlessSomethingWasSent()
    {
        await _h.AddPlayableAsync();
        var sync = Sync();

        await sync.FlushAsync(readBack: true, Token);
        await sync.FlushAsync(readBack: true, Token);
        Assert.Single(Reads);

        // The end of a play sends its last position and wants the server's word on it.
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        await sync.FlushAsync(readBack: true, Token);
        Assert.Equal(2, Reads.Count);

        _h.Time.Advance(OfflineProgressSync.ReadBackInterval);
        await sync.FlushAsync(readBack: true, Token);
        Assert.Equal(3, Reads.Count);
    }

    [Fact]
    public async Task AProfileThatBecomesActive_AndALinkThatComesBack_Flush()
    {
        await _h.AddPlayableAsync();
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        var sync = Sync();
        sync.Start();

        // The start itself looks once, after the settle.
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Sends.Count == 1 && Reads.Count == 1, "the start flushed and read back");

        _queue.Record(Scope7, "ep_5", 20_000, 0);
        _h.Network.SetMetered(false);
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Sends.Count == 2, "the link's change flushed");

        _queue.Record(Scope7, "ep_5", 30_000, 0);
        _h.Session.RaiseChanged();
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Sends.Count == 3, "the session's change flushed");
    }

    [Fact]
    public async Task FlushSoon_WaitsForTheSettle_AndTakesAStopsWishToReadBack()
    {
        await _h.AddPlayableAsync();
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        var sync = Sync();

        sync.FlushSoon();
        sync.FlushSoon(readBack: true);
        Assert.Empty(_h.Silo.Sent);
        _h.Time.Advance(OfflineProgressSync.Settle);
        await Until(() => Reads.Count == 1, "the flush ran and read back");

        Assert.Single(Sends);
    }

    [Fact]
    public async Task AfterDispose_NothingIsSent()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        var sync = Sync();
        sync.Start();

        sync.Dispose();
        _h.Session.RaiseChanged();
        _h.Network.SetMetered(true);
        _h.Time.Advance(OfflineProgressSync.RetryInterval);
        await sync.LatestRun;

        Assert.Empty(_h.Silo.Sent);
    }

    [Fact]
    public async Task ASignedOutSession_SendsNothing()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.SignOut();

        await Sync().FlushAsync(readBack: true, Token);

        Assert.Empty(_h.Silo.Sent);
        Assert.Single(_queue.Entries(Scope7));
    }
}
