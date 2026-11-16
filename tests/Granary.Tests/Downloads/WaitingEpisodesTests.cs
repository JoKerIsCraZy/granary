using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Granary.Core.Api;
using Granary.Core.Downloads;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// A season or a series past the server's limit of downloads for the account (429 <c>rate_limited</c>, issue #166): the episode
/// it refused and the ones after it wait on this PC, in order, for their profile, across starts, and are asked for again once a
/// download of the account leaves its preparation or goes, or after a wait that grows while the server still refuses; never in
/// a loop.
/// </summary>
public sealed class WaitingEpisodesTests : IAsyncDisposable
{
    private const string CreatePath = "api/v2/downloads";

    private static readonly SeriesDownloadRequest Series = new("sr_1", "Example Series",
    [
        new(1, [new("ep_1", 1, "Pilot"), new("ep_2", 2, "Chapter Two", "4572")]),
        new(2, [new("ep_21", 1, "Chapter Eleven")]),
    ], "original");

    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private static Func<Task<HttpResponseMessage>> Created(string id, string episode, string status = "ready") =>
        ScriptedSilo.Json(HttpStatusCode.Accepted, DownloadPayloads.Page(null, DownloadPayloads.Entry(id, status, episodeId: episode)));

    private static Func<Task<HttpResponseMessage>> Limit(TimeSpan? retryAfter = null) => ScriptedSilo.Problem(429, ProblemCodes.RateLimited, retryAfter);

    private void Creates(params Func<Task<HttpResponseMessage>>[] answers) => _h.Silo.On(HttpMethod.Post, CreatePath, answers);

    private List<string?> CreatedEpisodes() =>
        [.. _h.Silo.SentTo(HttpMethod.Post, CreatePath).Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("episode_id").GetString())];

    private string[] Waiting(DownloadScope? scope = null) => [.. _h.Store.WaitingIn(scope ?? Scope7).Select(w => w.EpisodeId)];

    // The series started until the server refused its second episode for the limit.
    private async Task<DownloadManager> LimitedAsync()
    {
        _h.List();
        Creates(Created("dl_1", "ep_1"), Limit());
        var manager = _h.NewManager();
        var result = await manager.StartSeriesAsync(Series, Token);
        Assert.Equal(new SeasonStartResult(Started: 1, AlreadyThere: 0, Failed: 0, ReachedLimit: true, Waiting: 2), result);
        return manager;
    }

    private async Task AdvanceAsync(TimeSpan by, DownloadManager manager)
    {
        _h.Time.Advance(by);
        await manager.WaitingRun;
    }

    [Fact]
    public async Task ALimitedSeries_KeepsTheRefusedEpisodeAndTheRestWaiting_InOrder()
    {
        await LimitedAsync();

        Assert.Equal(["ep_1", "ep_2"], CreatedEpisodes());
        Assert.Equal(["ep_2", "ep_21"], Waiting());
        var second = _h.Store.WaitingIn(Scope7)[0];
        Assert.Equal(("sr_1", "Example Series", 1, 2, "Chapter Two", "4572", "original"),
            (second.SeriesId, second.SeriesTitle, second.SeasonNumber, second.EpisodeNumber, second.Title, second.MediaFileId, second.Quality));
    }

    [Fact]
    public async Task TheWaitingEpisodes_AreAskedForAgainAfterAMinute_InOrder()
    {
        var manager = await LimitedAsync();
        Creates(Created("dl_2", "ep_2"), Created("dl_21", "ep_21"));

        await AdvanceAsync(TimeSpan.FromSeconds(59), manager);
        Assert.Equal(2, CreatedEpisodes().Count);

        await AdvanceAsync(TimeSpan.FromSeconds(1), manager);

        Assert.Equal(["ep_1", "ep_2", "ep_2", "ep_21"], CreatedEpisodes());
        Assert.Empty(Waiting());
        Assert.Equal("Chapter Two", _h.Find("dl_2")!.Record.Title);
        Assert.Equal("4572", JsonDocument.Parse(_h.Silo.SentTo(HttpMethod.Post, CreatePath)[2].Body!).RootElement.GetProperty("media_file_id").GetString());
        Assert.Equal(2, _h.Find("dl_21")!.Record.SeasonNumber);
    }

    // The period's limit frees nothing as downloads finish: the server is asked once per wait, each wait longer, never in a loop.
    [Fact]
    public async Task WhileTheServerStillRefuses_EachWaitIsLonger()
    {
        var manager = await LimitedAsync();

        await AdvanceAsync(TimeSpan.FromMinutes(1), manager);
        Assert.Equal(3, CreatedEpisodes().Count);
        await AdvanceAsync(TimeSpan.FromSeconds(119), manager);
        Assert.Equal(3, CreatedEpisodes().Count);
        await AdvanceAsync(TimeSpan.FromSeconds(1), manager);
        Assert.Equal(4, CreatedEpisodes().Count);
        await AdvanceAsync(TimeSpan.FromMinutes(5), manager);
        await AdvanceAsync(TimeSpan.FromMinutes(10), manager);
        await AdvanceAsync(TimeSpan.FromMinutes(30), manager);
        await AdvanceAsync(TimeSpan.FromMinutes(30), manager);

        Assert.Equal(8, CreatedEpisodes().Count);
        Assert.Equal(["ep_2", "ep_21"], Waiting());
    }

    [Fact]
    public async Task AWaitTheServerNames_IsKept()
    {
        _h.List();
        Creates(Created("dl_1", "ep_1"), Limit(TimeSpan.FromMinutes(10)));
        var manager = _h.NewManager();
        await manager.StartSeriesAsync(Series, Token);
        Creates(Created("dl_2", "ep_2"), Created("dl_21", "ep_21"));

        await AdvanceAsync(TimeSpan.FromMinutes(9), manager);
        Assert.Equal(2, CreatedEpisodes().Count);
        await AdvanceAsync(TimeSpan.FromMinutes(1), manager);

        Assert.Empty(Waiting());
    }

    // The account's limit of downloads at once counts the ones the server prepares: one that is ready frees a place.
    [Fact]
    public async Task ADownloadThatLeavesItsPreparation_LetsTheNextOneTryAtOnce()
    {
        await _h.AddAsync("dl_0", DownloadState.Preparing, mediaItemId: "mv_9");
        var manager = await LimitedAsync();
        Creates(Created("dl_2", "ep_2"), Limit());

        await _h.Store.UpdateAsync(Scope7, "dl_0", r => r with { State = DownloadState.Queued }, Token);
        await manager.WaitingRun;

        Assert.Equal(["ep_1", "ep_2", "ep_2", "ep_21"], CreatedEpisodes());
        Assert.Equal(["ep_21"], Waiting());
    }

    // The server drops a device's deleted entry, which then counts toward neither limit: once its delete reached the server,
    // not as it leaves this PC.
    [Fact]
    public async Task ADeleteThatReachedTheServer_LetsTheNextOneTryAtOnce()
    {
        await _h.AddAsync("dl_0", DownloadState.Completed, mediaItemId: "mv_9");
        var manager = await LimitedAsync();
        Creates(Created("dl_2", "ep_2"), Created("dl_21", "ep_21"));

        await _h.Store.RemoveAsync(Scope7, "dl_0", queueServerDelete: true, Token);
        await manager.WaitingRun;
        Assert.Equal(2, CreatedEpisodes().Count);

        await _h.Store.ForgetPendingDeleteAsync(_h.Store.PendingDeletes(Scope7).Single(), Token);
        await manager.WaitingRun;

        Assert.Empty(Waiting());
    }

    [Fact]
    public async Task AnotherStoreChange_DoesNotAskBeforeTheWait()
    {
        await _h.AddAsync("dl_0", DownloadState.Downloading, mediaItemId: "mv_9");
        var manager = await LimitedAsync();

        await _h.Store.UpdateAsync(Scope7, "dl_0", r => r with { State = DownloadState.Completed }, Token);
        await manager.WaitingRun;

        Assert.Equal(2, CreatedEpisodes().Count);
    }

    [Fact]
    public async Task AfterARestart_TheWaitingEpisodesAreAskedForOnce_ThenWaitAgain()
    {
        await LimitedAsync();
        Creates(Limit());

        await _h.RestartAsync();
        var manager = _h.NewManager();
        await manager.WaitingRun;

        Assert.Equal(3, CreatedEpisodes().Count);
        Assert.Equal(["ep_2", "ep_21"], Waiting());
        await AdvanceAsync(TimeSpan.FromSeconds(59), manager);
        Assert.Equal(3, CreatedEpisodes().Count);
    }

    // They are profile 7's: profile 8 does not download them, and they wait for profile 7.
    [Fact]
    public async Task AnotherProfile_LeavesThemWaiting_ForTheirOwn()
    {
        var manager = await LimitedAsync();
        Creates(Created("dl_2", "ep_2"), Created("dl_21", "ep_21"));

        _h.SwitchProfile("8");
        await AdvanceAsync(TimeSpan.FromMinutes(1), manager);
        Assert.Equal(2, CreatedEpisodes().Count);

        _h.SwitchProfile("7");
        await manager.WaitingRun;

        Assert.Empty(Waiting());
        Assert.All(_h.Silo.SentTo(HttpMethod.Post, CreatePath), r => Assert.Equal("7", r.AsProfile));
    }

    [Fact]
    public async Task AnEpisodeTheServerNoLongerOffers_LeavesTheLine()
    {
        var manager = await LimitedAsync();
        Creates(ScriptedSilo.Problem(404, "not_found"), Created("dl_21", "ep_21"));

        await AdvanceAsync(TimeSpan.FromMinutes(1), manager);

        Assert.Empty(Waiting());
        Assert.NotNull(_h.Find("dl_21"));
    }

    // Another 429, such as a proxy's, is no account limit: the series fails there as before and nothing waits.
    [Fact]
    public async Task A429WithoutTheLimitsCode_KeepsNothingWaiting()
    {
        _h.List();
        Creates(Created("dl_1", "ep_1"), ScriptedSilo.Problem(429, "too_many_requests"));
        var manager = _h.NewManager();

        var result = await manager.StartSeriesAsync(Series, Token);

        Assert.Equal(new SeasonStartResult(Started: 1, AlreadyThere: 0, Failed: 2), result);
        Assert.Empty(Waiting());
    }

    // A second press of Download sends nothing for the waiting episodes: they are on their way.
    [Fact]
    public async Task TheSameSeriesStartedAgain_CountsTheWaitingEpisodesAsThere()
    {
        var manager = await LimitedAsync();

        var again = await manager.StartSeriesAsync(Series, Token);

        Assert.Equal(new SeasonStartResult(Started: 0, AlreadyThere: 3, Failed: 0), again);
        Assert.Equal(2, CreatedEpisodes().Count);
    }

    [Fact]
    public async Task DropWaiting_TakesASeriesEpisodesOutOfLine()
    {
        var manager = await LimitedAsync();

        await manager.DropWaitingAsync(Scope7, "sr_1", Token);
        await AdvanceAsync(TimeSpan.FromMinutes(1), manager);

        Assert.Empty(Waiting());
        Assert.Equal(2, CreatedEpisodes().Count);
    }

    // Review 1: a refusal the server will give again (a 4xx it means) drops the head, logged without its title or id; the next
    // episode goes.
    [Fact]
    public async Task AHeadTheServerRefusesForGood_LeavesTheLine_AndTheNextGoes()
    {
        var manager = await LimitedAsync();
        Creates(ScriptedSilo.Problem(422, "validation_failed"), Created("dl_21", "ep_21"));

        await AdvanceAsync(TimeSpan.FromMinutes(1), manager);

        Assert.Empty(Waiting());
        Assert.NotNull(_h.Find("dl_21"));
        Assert.DoesNotContain(_h.Logs.Lines, line => line.Contains("Chapter Two", StringComparison.Ordinal) || Regex.IsMatch(line, @"\bep_2\b"));
    }

    // Review 1: an answer that cannot be read is no Silo answer (a proxy's or a sign-in page's, SiloHttp), so the head is not
    // dropped for it; after three such tries in a row it goes to the back of the line, and the others are not held back.
    [Fact]
    public async Task AHeadThatKeepsFailing_GoesToTheBackOfTheLine_AfterThreeTries()
    {
        var manager = await LimitedAsync();
        var unreadable = ScriptedSilo.Json(HttpStatusCode.Accepted, "{ not json");
        Creates(unreadable, unreadable, unreadable, Created("dl_21", "ep_21"), Created("dl_2", "ep_2"));

        await AdvanceAsync(TimeSpan.FromMinutes(1), manager);
        await AdvanceAsync(TimeSpan.FromMinutes(2), manager);
        Assert.Equal(["ep_2", "ep_21"], Waiting());
        await AdvanceAsync(TimeSpan.FromMinutes(5), manager);
        Assert.Equal(["ep_21", "ep_2"], Waiting());
        await AdvanceAsync(TimeSpan.FromMinutes(10), manager);

        Assert.Empty(Waiting());
        Assert.Equal(["ep_1", "ep_2", "ep_2", "ep_2", "ep_2", "ep_21", "ep_2"], CreatedEpisodes());
        Assert.DoesNotContain(_h.Logs.Lines, line => line.Contains("Chapter Two", StringComparison.Ordinal) || Regex.IsMatch(line, @"\bep_2\b"));
    }

    // A create without an answer may have reached the server: the head stays, and the line waits.
    [Fact]
    public async Task AHeadWithoutAnAnswer_StaysAtTheHead_AndTheLineWaits()
    {
        var manager = await LimitedAsync();
        Creates(ScriptedSilo.NetworkError());

        await AdvanceAsync(TimeSpan.FromMinutes(1), manager);
        Assert.Equal(["ep_2", "ep_21"], Waiting());
        Assert.Equal(3, CreatedEpisodes().Count);
        await AdvanceAsync(TimeSpan.FromSeconds(119), manager);

        Assert.Equal(3, CreatedEpisodes().Count);
    }

    // Review 2: Cancel while a create is under way, answered with the limit: nothing is kept waiting to download later.
    [Fact]
    public async Task ACancelBeforeTheLimitsAnswer_KeepsNothingWaiting()
    {
        _h.List();
        var (held, gate) = ItemDetailFixture.Held();
        Creates(Created("dl_1", "ep_1"), held);
        var manager = _h.NewManager();
        using var cancel = new CancellationTokenSource();

        var run = manager.StartSeriesAsync(Series, cancel.Token);
        await Until(() => CreatedEpisodes().Count == 2, "the second episode's create was sent");
        cancel.Cancel();
        gate.SetResult(await Limit()());
        var result = await run;

        Assert.Equal(0, result.Waiting);
        Assert.Empty(Waiting());
        await AdvanceAsync(TimeSpan.FromHours(1), manager);
        Assert.Equal(2, CreatedEpisodes().Count);
    }

    // Review 3: a wait the server names is kept to half an hour, a delay or a date alike, and the episodes wait all the same.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWaitTheServerNamesFarAhead_IsKeptToHalfAnHour(bool asDate)
    {
        _h.List();
        Creates(Created("dl_1", "ep_1"), asDate ? LimitUntil(DateTimeOffset.UtcNow.AddYears(5)) : Limit(TimeSpan.FromSeconds(99_999_999)));
        var manager = _h.NewManager();

        var result = await manager.StartSeriesAsync(Series, Token);
        Assert.Equal(2, result.Waiting);
        Assert.Equal(["ep_2", "ep_21"], Waiting());
        Creates(Created("dl_2", "ep_2"), Created("dl_21", "ep_21"));

        await AdvanceAsync(TimeSpan.FromMinutes(29), manager);
        Assert.Equal(2, CreatedEpisodes().Count);
        await AdvanceAsync(TimeSpan.FromMinutes(1), manager);

        Assert.Empty(Waiting());
    }

    // Review 4: a place that frees while the head is asked is not lost when that ask is refused: the line is asked again.
    [Fact]
    public async Task APlaceFreedWhileTheHeadIsAsked_IsNotLost()
    {
        await _h.AddAsync("dl_0", DownloadState.Preparing, mediaItemId: "mv_9");
        var manager = await LimitedAsync();
        var (held, gate) = ItemDetailFixture.Held();
        Creates(held, Created("dl_2", "ep_2"), Created("dl_21", "ep_21"));

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await Until(() => CreatedEpisodes().Count == 3, "the head was asked again");
        await _h.Store.UpdateAsync(Scope7, "dl_0", r => r with { State = DownloadState.Queued }, Token);
        gate.SetResult(await Limit()());
        await manager.WaitingRun;

        Assert.Empty(Waiting());
        Assert.Equal(["ep_1", "ep_2", "ep_2", "ep_2", "ep_21"], CreatedEpisodes());
    }

    // Review 5: a timer that comes before the wait is over (the clock stepped back) is set again for the rest of it.
    [Fact]
    public async Task ATimerThatComesEarly_IsSetAgainForTheRestOfTheWait()
    {
        var time = new EarlyOnceTimeProvider(_h.Time, TimeSpan.FromSeconds(10));
        _h.List();
        Creates(Created("dl_1", "ep_1"), Limit());
        var manager = new DownloadManager(_h.Store, _h.NewQueue(), _h.Api, _h.NewFeature(), _h.Session, new FakeDeviceProbe(),
            new InMemoryPlaybackDevicePreferences(), time, _h.Logs.For<DownloadManager>(), _h.Playing);
        await manager.StartSeriesAsync(Series, Token);
        Creates(Created("dl_2", "ep_2"), Created("dl_21", "ep_21"));

        await AdvanceAsync(TimeSpan.FromSeconds(50), manager);
        Assert.Equal(2, CreatedEpisodes().Count);
        await AdvanceAsync(TimeSpan.FromSeconds(10), manager);

        Assert.Empty(Waiting());
    }

    // Review 8: an episode the viewer starts by hand while it waits stays in line until its download is here: that start
    // can still fail or meet the limit.
    [Fact]
    public async Task AnEpisodeStartedByHandWhileItWaits_StaysInLine_UntilItsDownloadIsHere()
    {
        var manager = await LimitedAsync();
        var (held, gate) = ItemDetailFixture.Held();
        Creates(held);

        var byHand = manager.StartAsync(new DownloadRequest("ep_2", "sr_1", "4572", "original", "Chapter Two", "Example Series", 1, 2), Token);
        await Until(() => CreatedEpisodes().Count == 3, "the start by hand was sent");
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await manager.WaitingRun.WaitAsync(TimeSpan.FromSeconds(5), Token);
        gate.SetResult(await Limit()());

        Assert.Equal(DownloadStartResult.Limited, await byHand);
        Assert.Equal(["ep_2", "ep_21"], Waiting());
    }

    // A sign-out while the line's head is asked: the server's answer keeps nothing, and nothing waits for the account.
    [Fact]
    public async Task ASignOutWhileTheHeadIsAsked_KeepsNothing()
    {
        var manager = await LimitedAsync();
        var (held, gate) = ItemDetailFixture.Held();
        Creates(held);

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await Until(() => CreatedEpisodes().Count == 3, "the head was asked again");
        await _h.Store.RemoveWhereAsync(scope => scope.AccountId == "1", _h.Store.MarkCleared("srv1", "1"), Token);
        gate.SetResult(await Created("dl_2", "ep_2")());
        await manager.WaitingRun;

        Assert.Empty(Waiting());
        Assert.Null(_h.Find("dl_2"));
    }

    // The dialog's count is the series' whole line: episodes that waited already are in it too.
    [Fact]
    public async Task TheWaitingCount_IsTheSeriesWholeLine()
    {
        await _h.Store.LoadAsync(Token);
        await _h.Store.AddWaitingUnlessClearedAsync([new WaitingEpisode(Scope7, "sr_1", "Example Series", 2, "ep_21", 1, "Chapter Eleven", null, "original")],
            _h.Store.ClearGeneration, Token);
        _h.List();
        Creates(Limit(), Created("dl_1", "ep_1"), Limit());
        var manager = _h.NewManager();
        await manager.WaitingRun;

        var result = await manager.StartSeriesAsync(Series, Token);

        Assert.Equal(new SeasonStartResult(Started: 1, AlreadyThere: 0, Failed: 0, ReachedLimit: true, Waiting: 2), result);
    }

    private static Func<Task<HttpResponseMessage>> LimitUntil(DateTimeOffset date) => async () =>
    {
        var response = await Limit()();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(date);
        return response;
    };

    // The clock of the harness, whose first timer fires a little before it is due, as after the wall clock stepped back.
    private sealed class EarlyOnceTimeProvider(ManualTimeProvider time, TimeSpan early) : TimeProvider
    {
        private int _timers;

        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();

        public override long GetTimestamp() => time.GetTimestamp();

        public override long TimestampFrequency => time.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => time.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            time.CreateTimer(callback, state, Interlocked.Increment(ref _timers) == 1 && dueTime > early ? dueTime - early : dueTime, period);
    }
}
