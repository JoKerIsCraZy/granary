using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Downloads;
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
}
