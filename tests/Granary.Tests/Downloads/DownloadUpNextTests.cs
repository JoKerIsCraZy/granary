using System.Net;
using Granary.Core.Api.Catalog;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Downloads;

/// <summary>The episode after a downloaded one: the server's while it answers, else the next one on this PC.</summary>
public sealed class DownloadUpNextTests : IAsyncDisposable
{
    private const string Season1 = "api/v2/catalog/series/sr_1/seasons/1/episodes";
    private const string Seasons = "api/v2/catalog/series/sr_1/seasons";
    private const string Home = "api/v2/home/sections";

    private static readonly PlayingEpisode Episode5 = new("ep_5", "sr_1", "Example Series", 1, 5);

    private readonly DownloadHarness _h = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private DownloadUpNext Create() => new(
        new UpNextSource(new CatalogApi(_h.Silo.Http), _h.Session, _h.Logs.For<UpNextSource>()), _h.Store, _h.Session, _h.Time);

    private static string Episodes(int season, params int[] numbers) =>
        "{ \"items\": [" + string.Join(",", numbers.Select(n =>
            $$"""{ "content_id": "ep_{{n}}", "season_number": {{season}}, "episode_number": {{n}}, "title": "Chapter {{n}}", "files": [ { "file_id": "f{{n}}" } ] }""")) + "] }";

    private void Offline()
    {
        _h.Silo.On(HttpMethod.Get, Season1, ScriptedSilo.NetworkError());
        _h.Silo.On(HttpMethod.Get, Seasons, ScriptedSilo.NetworkError());
    }

    // Season 1's episodes 5 and 7 and season 2's first, on this PC; episode 6 is not.
    private async Task AddDownloadsAsync()
    {
        await _h.AddPlayableAsync("dl_5", "ep_5", episode: 5);
        await _h.AddPlayableAsync("dl_7", "ep_7", episode: 7, change: r => r with { Title = "Chapter Seven" });
        await _h.AddPlayableAsync("dl_21", "ep_21", episode: 1, season: 2, change: r => r with { Title = "Second Chapter One" });
    }

    [Fact]
    public async Task TheServersNextEpisode_ComesFirst()
    {
        await AddDownloadsAsync();
        _h.Silo.On(HttpMethod.Get, Season1, ScriptedSilo.Json(HttpStatusCode.OK, Episodes(1, 5, 6, 7)));

        var next = await Create().NextEpisodeAsync(Episode5, Token);

        // Episode 6 is not on this PC: it streams, as the series goes on in order.
        Assert.Equal(("ep_6", 1, 6), (next!.ContentId, next.SeasonNumber, next.EpisodeNumber));
    }

    [Fact]
    public async Task WithoutTheServer_TheNextDownloadedEpisodePlays()
    {
        await AddDownloadsAsync();
        Offline();

        var next = await Create().NextEpisodeAsync(Episode5, Token);

        Assert.Equal(new NextEpisode("ep_7", 1, 7, "Chapter Seven"), next);
        Assert.Null(next!.StillUrl);
    }

    [Fact]
    public async Task WithoutTheServer_TheNextSeasonsDownloadFollowsTheSeasonsLast()
    {
        await AddDownloadsAsync();
        Offline();

        var next = await Create().NextEpisodeAsync(Episode5 with { ContentId = "ep_7", EpisodeNumber = 7 }, Token);

        Assert.Equal(new NextEpisode("ep_21", 2, 1, "Second Chapter One"), next);
    }

    [Fact]
    public async Task WithoutTheServer_AtTheLastDownloadedEpisode_NothingIsNext()
    {
        await AddDownloadsAsync();
        Offline();

        Assert.Null(await Create().NextEpisodeAsync(new PlayingEpisode("ep_21", "sr_1", "Example Series", 2, 1), Token));
    }

    [Fact]
    public async Task OnlyDownloadsThatCanPlay_AndOfThisSeriesAndProfile_Follow()
    {
        await _h.AddPlayableAsync("dl_5", "ep_5", episode: 5);
        await _h.AddPlayableAsync("dl_6", "ep_6", episode: 6, change: r => r with { State = DownloadState.Downloading, MediaFile = null });
        await _h.AddPlayableAsync("dl_7", "ep_7", episode: 7, change: r => r with { SeriesId = "sr_other" });
        await _h.AddPlayableAsync("dl_8", "ep_8", episode: 8, change: r => r with { Scope = DownloadHarness.Scope8 });
        await _h.AddPlayableAsync("dl_9", "ep_9", episode: 9, change: r => r with { Title = "Chapter Nine" });
        Offline();

        var next = await Create().NextEpisodeAsync(Episode5, Token);

        Assert.Equal("ep_9", next?.ContentId);
    }

    [Fact]
    public async Task WithoutTheServer_ThePreviousDownloadedEpisodeOfTheSeason()
    {
        await AddDownloadsAsync();
        Offline();
        var source = Create();

        var previous = await source.PreviousEpisodeAsync(Episode5 with { ContentId = "ep_7", EpisodeNumber = 7 }, Token);

        Assert.Equal(new NextEpisode("ep_5", 1, 5, "Chapter Five"), previous);
        // None before a season's first, as on the web.
        Assert.Null(await source.PreviousEpisodeAsync(new PlayingEpisode("ep_21", "sr_1", "Example Series", 2, 1), Token));
    }

    [Fact]
    public async Task AServerThatDoesNotAnswer_HoldsTheNextEpisodeFiveSecondsAtMost()
    {
        await AddDownloadsAsync();
        _h.Silo.OnCancellable(HttpMethod.Get, Season1, async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        });

        var next = Create().NextEpisodeAsync(Episode5, Token);
        Assert.False(next.IsCompleted);
        _h.Time.Advance(DownloadUpNext.ServerBudget);

        Assert.Equal("ep_7", (await next.WaitAsync(TimeSpan.FromSeconds(5), Token))?.ContentId);
    }

    [Fact]
    public async Task ASlowServer_IsWaitedFor_WhenNoDownloadFollows()
    {
        // A title that streams, on a server that takes its time: its next episode still comes.
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Silo.On(HttpMethod.Get, Season1, () => answer.Task);

        var next = Create().NextEpisodeAsync(Episode5, Token);
        _h.Time.Advance(DownloadUpNext.ServerBudget + TimeSpan.FromSeconds(30));
        Assert.False(next.IsCompleted);
        answer.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, Episodes(1, 5, 6))());

        Assert.Equal("ep_6", (await next.WaitAsync(TimeSpan.FromSeconds(5), Token))?.ContentId);
    }

    [Fact]
    public async Task OnDeck_IsTheServers()
    {
        await AddDownloadsAsync();
        _h.Silo.On(HttpMethod.Get, Home, ScriptedSilo.Json(HttpStatusCode.OK, """{ "sections": [] }"""));

        Assert.Empty(await Create().OnDeckAsync("sr_1", Token));
        Assert.Single(_h.Silo.SentTo(HttpMethod.Get, Home));
    }
}
