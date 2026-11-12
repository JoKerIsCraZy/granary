using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
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

    public OfflineProgressSyncTests()
    {
        _queue = new OfflineProgressQueue(Path.Combine(_h.Directory, "data", "offline-progress.json"), _h.Time, _h.Logs.For<OfflineProgressQueue>());
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
        var sync = new OfflineProgressSync(_queue, new ProgressSyncApi(_h.Silo.Http), _h.Store, _h.Session, _h.Network, _h.Time, _h.Logs.For<OfflineProgressSync>());
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
        Assert.Equal("7", sent.AsProfile);
        Assert.Empty(_queue.Entries(Scope7));
        // A send alone asks for nothing back.
        Assert.Empty(Reads);
    }

    [Fact]
    public async Task SucceededAndFailedItems_LeaveTheQueue()
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _queue.Record(Scope7, "ep_gone", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, ScriptedSilo.Json(HttpStatusCode.OK, """
            { "items": [ { "index": 0, "media_item_id": "ep_5", "status": "success" },
                         { "index": 1, "media_item_id": "ep_gone", "status": "failure", "failure": { "type": "x", "title": "Not found", "status": 404 } } ],
              "summary": { "total": 2, "succeeded": 1, "failed": 1 } }
            """));

        await Sync().FlushAsync(readBack: false, Token);

        // A title the server no longer has will not be taken at a second try either.
        Assert.Empty(_queue.Entries(Scope7));
        Assert.Single(Sends);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task ABusyOrRefusingServer_KeepsTheEntriesForLater(int status)
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
    [InlineData("malformed")]
    [InlineData("500")]
    [InlineData("504")]
    [InlineData("short")]
    public async Task ASendWithNoUsableAnswer_IsHeld_AndNeverSentAgain(string answer)
    {
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath, answer switch
        {
            "ended" => () => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "the connection broke off")),
            "malformed" => ScriptedSilo.Json(HttpStatusCode.OK, "{ not json"),
            "500" => ScriptedSilo.Problem(500, "internal_error"),
            "504" => ScriptedSilo.Problem(504, "gateway_timeout"),
            // An answer for fewer items than were sent says nothing sure about any of them.
            _ => ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [] }"""),
        }, Answered("ep_5"));
        var sync = Sync();

        await sync.FlushAsync(readBack: false, Token);
        await sync.FlushAsync(readBack: false, Token);

        // The server may have taken it: a second send could count it twice.
        Assert.Equal(OfflineProgressState.Held, Assert.Single(_queue.Entries(Scope7)).State);
        Assert.Single(Sends);
    }

    [Fact]
    public async Task AHeldEntry_GoesOnceTheServersProgressWasRead_AndANewerPositionIsSent()
    {
        await _h.AddPlayableAsync();
        _queue.Record(Scope7, "ep_5", 10_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath,
            () => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "the connection broke off")),
            Answered("ep_5"));
        var sync = Sync();
        await sync.FlushAsync(readBack: false, Token);

        // The viewer watches on: the newer position is another entry, and is sent.
        _h.Time.Advance(TimeSpan.FromSeconds(10));
        _queue.Record(Scope7, "ep_5", 20_000, 0);
        _queue.Record(Scope7, "mv_1", 5_000, 0);
        _h.Silo.On(HttpMethod.Post, SyncPath,
            () => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ResponseEnded, "the connection broke off")));
        await sync.FlushAsync(readBack: false, Token);
        Assert.All(_queue.Entries(Scope7), e => Assert.Equal(OfflineProgressState.Held, e.State));

        await sync.FlushAsync(readBack: true, Token);

        Assert.Empty(_queue.Entries(Scope7));
        Assert.Single(Reads);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
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
    public async Task AServerPositionOlderThanThisPCs_LeavesThisPCsAlone()
    {
        await _h.AddPlayableAsync(change: r => r with { ResumeMs = 3_000_000, ResumeAt = ManualTimeProvider.DefaultStart.AddHours(1) });
        _h.Silo.On(HttpMethod.Get, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, Listing(null, Row("ep_5", 1200, "2026-10-01T12:00:00Z"))));

        await Sync().FlushAsync(readBack: true, Token);

        var record = _h.Find("dl_1")!.Record;
        Assert.Equal((3_000_000L, ManualTimeProvider.DefaultStart.AddHours(1)), (record.ResumeMs, record.ResumeAt));
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
        Assert.All(Reads, r => Assert.Equal("7", r.AsProfile));
        Assert.Equal((600_000L, false), (_h.Find("dl_5")!.Record.ResumeMs, _h.Find("dl_5")!.Record.IsWatched));
        Assert.Equal((0L, true), (_h.Find("dl_7")!.Record.ResumeMs, _h.Find("dl_7")!.Record.IsWatched));
    }

    [Fact]
    public async Task AReadBackThatBreaksOff_ChangesNothing_AndKeepsWhatIsHeld()
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
        Assert.Equal(OfflineProgressState.Held, Assert.Single(_queue.Entries(Scope7)).State);
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
