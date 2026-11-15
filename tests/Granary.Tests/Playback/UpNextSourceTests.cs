using System.Net;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Catalog;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>The next episode as the apps resolve it (docs/api-contract.md, 10.7), and On Deck from the Home row.</summary>
public sealed class UpNextSourceTests
{
    private const string Season1 = "api/v2/catalog/series/ted/seasons/1/episodes";
    private const string Season2 = "api/v2/catalog/series/ted/seasons/2/episodes";
    private const string Seasons = "api/v2/catalog/series/ted/seasons";

    private static readonly PlayingEpisode LastOfSeason1 = new("ted-1-2", "ted", "ted", 1, 2);

    private readonly ScriptedSilo _silo = new();
    private readonly LogSink _logs = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private UpNextSource Create() => new(new CatalogApi(_silo.Http),
        new FakeSessionManager { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" },
        new ImageParameterCache(new ImagesApi(_silo.Http), _logs.For<ImageParameterCache>()), _logs.For<UpNextSource>());

    // One query parameter's value, whole: "still_width=712" is not "still_width=7120".
    private static string? QueryValue(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair[0] == name)
            .Select(pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "")
            .SingleOrDefault();

    private static string Episodes(int season, params int[] numbers) =>
        "{ \"items\": [" + string.Join(",", numbers.Select(n =>
            $$"""{ "content_id": "ted-{{season}}-{{n}}", "season_number": {{season}}, "episode_number": {{n}}, "title": "Episode {{n}}", "runtime": 37, "still_url": "/s/{{n}}.jpg" }""")) + "] }";

    // Episodes with a file each; the server could not read the files of those in unreadable.
    private static string EpisodesWithFiles(int season, int[] numbers, params int[] unreadable) =>
        "{ \"items\": [" + string.Join(",", numbers.Select(n =>
            $$"""{ "content_id": "ted-{{season}}-{{n}}", "season_number": {{season}}, "episode_number": {{n}}, "title": "Episode {{n}}", "files": [ { "file_id": "f{{n}}", "unreadable": {{(unreadable.Contains(n) ? "true" : "false")}} } ] }""")) + "] }";

    [Fact]
    public async Task ThePreviousEpisode_IsTheOneBeforeInTheSeason_PastThoseThatCannotPlay()
    {
        _silo.On(HttpMethod.Get, Season1, ScriptedSilo.Json(HttpStatusCode.OK, EpisodesWithFiles(1, [4, 1, 3, 2], unreadable: 3)));

        var previous = await Create().PreviousEpisodeAsync(LastOfSeason1 with { EpisodeNumber = 4, ContentId = "ted-1-4" }, null, Token);

        Assert.Equal("ted-1-2", previous?.ContentId);
        Assert.Single(_silo.Sent);
    }

    [Fact]
    public async Task TheNextAndThePreviousEpisode_AskedForTogether_ReadTheSeasonOnce()
    {
        var answer = new TaskCompletionSource();
        _silo.On(HttpMethod.Get, Season1, async () =>
        {
            await answer.Task;
            return await ScriptedSilo.Json(HttpStatusCode.OK, EpisodesWithFiles(1, [1, 2, 3]))();
        });
        var source = Create();
        var current = LastOfSeason1 with { ContentId = "ted-1-2" };

        var next = source.NextEpisodeAsync(current, null, Token);
        var previous = source.PreviousEpisodeAsync(current, null, Token);
        answer.SetResult();

        Assert.Equal("ted-1-3", (await next)?.ContentId);
        Assert.Equal("ted-1-1", (await previous)?.ContentId);
        Assert.Single(_silo.Sent);
    }

    [Fact]
    public async Task TheSeasonsFirstEpisode_HasNoPreviousOne_AsOnTheWeb()
    {
        _silo.On(HttpMethod.Get, Season2, ScriptedSilo.Json(HttpStatusCode.OK, EpisodesWithFiles(2, [1, 2])));

        var previous = await Create().PreviousEpisodeAsync(new PlayingEpisode("ted-2-1", "ted", "ted", 2, 1), null, Token);

        Assert.Null(previous);
        Assert.Single(_silo.Sent);
    }

    [Fact]
    public async Task TheNextEpisode_OfTheSameSeason_NeedsOneRead()
    {
        _silo.On(HttpMethod.Get, Season1, ScriptedSilo.Json(HttpStatusCode.OK, Episodes(1, 3, 1, 2)));

        var next = await Create().NextEpisodeAsync(LastOfSeason1 with { EpisodeNumber = 1, ContentId = "ted-1-1" }, null, Token);

        Assert.Equal(new NextEpisode("ted-1-2", 1, 2, "Episode 2") { RuntimeMinutes = 37, StillUrl = ArtworkUrl.Resolve(ScriptedSilo.Base, "/s/2.jpg") }, next);
        Assert.Single(_silo.Sent);
    }

    [Fact]
    public async Task AfterTheSeasonsLastEpisode_TheNextRegularSeasonFollows_NotTheSpecials()
    {
        _silo.On(HttpMethod.Get, Season1, ScriptedSilo.Json(HttpStatusCode.OK, Episodes(1, 1, 2)));
        _silo.On(HttpMethod.Get, Seasons, ScriptedSilo.Json(HttpStatusCode.OK, """
            { "items": [
              { "content_id": "s0", "season_number": 0, "is_specials": true },
              { "content_id": "s3", "season_number": 3 },
              { "content_id": "s1", "season_number": 1 },
              { "content_id": "s2", "season_number": 2 }
            ] }
            """));
        _silo.On(HttpMethod.Get, Season2, ScriptedSilo.Json(HttpStatusCode.OK, Episodes(2, 2, 1)));

        var next = await Create().NextEpisodeAsync(LastOfSeason1, null, Token);

        Assert.Equal("ted-2-1", next?.ContentId);
    }

    [Fact]
    public async Task TheSeason_IsReadWithStillsAsWideAsThePlayingNextScreenDrawsThem()
    {
        _silo.On(HttpMethod.Get, "api/v2/images/capabilities", ScriptedSilo.Json(HttpStatusCode.OK, CatalogPayloads.ImageCapabilities));
        _silo.On(HttpMethod.Get, Season1, ScriptedSilo.Json(HttpStatusCode.OK, Episodes(1, 1, 2, 3)));

        var next = await Create().NextEpisodeAsync(LastOfSeason1, new ImageSizes(Still: 1067), Token);

        Assert.Equal("ted-1-3", next?.ContentId);
        var read = Assert.Single(_silo.Sent, r => r.Uri.AbsolutePath.EndsWith("/seasons/1/episodes", StringComparison.Ordinal));
        Assert.Equal("1067", QueryValue(read.Uri, "still_width"));
    }

    [Fact]
    public async Task TheNextAndThePreviousEpisode_AskedForTogetherWithAWidth_ReadTheSeasonOnce_WhileTheServersSizesAreStillRead()
    {
        var capabilities = new TaskCompletionSource();
        _silo.On(HttpMethod.Get, "api/v2/images/capabilities", async () =>
        {
            await capabilities.Task;
            return await ScriptedSilo.Json(HttpStatusCode.OK, CatalogPayloads.ImageCapabilities)();
        });
        _silo.On(HttpMethod.Get, Season1, ScriptedSilo.Json(HttpStatusCode.OK, EpisodesWithFiles(1, [1, 2, 3])));
        var source = Create();
        var current = LastOfSeason1 with { ContentId = "ted-1-2" };
        var sizes = new ImageSizes(Still: 1067);

        var next = source.NextEpisodeAsync(current, sizes, Token);
        var previous = source.PreviousEpisodeAsync(current, sizes, Token);
        capabilities.SetResult();

        Assert.Equal("ted-1-3", (await next)?.ContentId);
        Assert.Equal("ted-1-1", (await previous)?.ContentId);
        var read = Assert.Single(_silo.Sent, r => r.Uri.AbsolutePath.EndsWith("/seasons/1/episodes", StringComparison.Ordinal));
        Assert.Equal("1067", QueryValue(read.Uri, "still_width"));
    }

    [Fact]
    public async Task ALaterSeason_IsReadWithTheSameWidth()
    {
        _silo.On(HttpMethod.Get, "api/v2/images/capabilities", ScriptedSilo.Json(HttpStatusCode.OK, CatalogPayloads.ImageCapabilities));
        _silo.On(HttpMethod.Get, Season1, ScriptedSilo.Json(HttpStatusCode.OK, Episodes(1, 1, 2)));
        _silo.On(HttpMethod.Get, Seasons, ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [ { "content_id": "s1", "season_number": 1 }, { "content_id": "s2", "season_number": 2 } ] }"""));
        _silo.On(HttpMethod.Get, Season2, ScriptedSilo.Json(HttpStatusCode.OK, Episodes(2, 1)));

        var next = await Create().NextEpisodeAsync(LastOfSeason1, new ImageSizes(Still: 712), Token);

        Assert.Equal("ted-2-1", next?.ContentId);
        var read = Assert.Single(_silo.Sent, r => r.Uri.AbsolutePath.EndsWith("/seasons/2/episodes", StringComparison.Ordinal));
        Assert.Equal("712", QueryValue(read.Uri, "still_width"));
    }

    [Fact]
    public async Task WithoutAWidth_TheSeasonIsReadAtTheServersSizes_AndNothingElseIsAsked()
    {
        _silo.On(HttpMethod.Get, Season1, ScriptedSilo.Json(HttpStatusCode.OK, Episodes(1, 1, 2, 3)));

        var next = await Create().NextEpisodeAsync(LastOfSeason1, null, Token);

        Assert.Equal("ted-1-3", next?.ContentId);
        var read = Assert.Single(_silo.Sent);
        Assert.Null(QueryValue(read.Uri, "still_width"));
        Assert.DoesNotContain("width", read.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLastEpisode_HasNone_AndAFailedReadIsNone()
    {
        _silo.On(HttpMethod.Get, Season1, ScriptedSilo.Json(HttpStatusCode.OK, Episodes(1, 1, 2)));
        _silo.On(HttpMethod.Get, Seasons, ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [ { "content_id": "s1", "season_number": 1 } ] }"""));
        Assert.Null(await Create().NextEpisodeAsync(LastOfSeason1, null, Token));

        _silo.On(HttpMethod.Get, Season1, ScriptedSilo.NetworkError());
        Assert.Null(await Create().NextEpisodeAsync(LastOfSeason1, null, Token));
        Assert.Contains(_logs.Lines, l => l.Contains("next episode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnDeck_IsContinueWatching_WithoutThisSeriesAndWithoutBooks()
    {
        _silo.On(HttpMethod.Get, "api/v2/home/sections", ScriptedSilo.Json(HttpStatusCode.OK, """
            { "sections": [
              { "id": "recent", "section_type": "recently_added", "title": "Recently Added", "total_count": 1,
                "items": [ { "content_id": "m-0", "type": "movie", "title": "Elsewhere" } ] },
              { "id": "cw", "section_type": "continue_watching", "title": "Continue Watching", "total_count": 4, "items": [
                { "content_id": "ted-1-4", "type": "episode", "title": "Episode 4", "series_id": "ted" },
                { "content_id": "m-1", "type": "movie", "title": "Alles Routine" },
                { "content_id": "b-1", "type": "audiobook", "title": "A Book" },
                { "content_id": "mob-2-6", "type": "episode", "title": "A Different Corner", "series_id": "mobland" }
              ] }
            ] }
            """));

        var cards = await Create().OnDeckAsync("ted", Token);

        Assert.Equal(["m-1", "mob-2-6"], cards.Select(c => c.Id));
    }

    [Fact]
    public async Task OnDeckCards_HaveNoLinks_AClickOnThemPlays()
    {
        _silo.On(HttpMethod.Get, "api/v2/home/sections", ScriptedSilo.Json(HttpStatusCode.OK, """
            { "sections": [
              { "id": "cw", "section_type": "continue_watching", "title": "Continue Watching", "total_count": 1, "items": [
                { "content_id": "mob-2-6", "type": "episode", "title": "A Different Corner", "series_id": "mobland", "series_title": "MobLand",
                  "season_number": 2, "episode_number": 6 }
              ] }
            ] }
            """));

        var card = Assert.Single(await Create().OnDeckAsync("ted", Token));

        // The player's row starts the title; its title line must not lead away to the series.
        Assert.Null(card.HeadingId);
        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Subtitle));
    }
}
