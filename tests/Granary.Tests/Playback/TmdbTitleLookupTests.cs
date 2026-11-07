using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>The catalog items and TMDB titles the Discord status reads.</summary>
internal static class TmdbPayloads
{
    public const string HeatId = "movie-heat";
    public const string DarkId = "series-dark";
    public const string HeatItemPath = "api/v2/catalog/items/movie-heat";
    public const string DarkItemPath = "api/v2/catalog/items/series-dark";
    public const string HeatDetailPath = "api/v2/requests/detail/movie/949";
    public const string DarkDetailPath = "api/v2/requests/detail/series/70523";

    public const string HeatItem = """
        { "content_id": "movie-heat", "type": "movie", "title": "Heat", "year": 1995, "tmdb_id": "949",
          "poster_url": "/api/v2/artwork/heat/poster.webp?sig=x" }
        """;

    public const string DarkItem = """{ "content_id": "series-dark", "type": "series", "title": "Dark", "tmdb_id": "70523" }""";

    public const string HeatDetail = """{ "tmdb_id": 949, "media_type": "movie", "title": "Heat", "year": 1995, "poster_path": "/heat.jpg" }""";

    public const string DarkDetail = """{ "tmdb_id": 70523, "media_type": "series", "title": "Dark", "poster_path": "/dark.jpg" }""";

    public static readonly Uri HeatPage = new("https://www.themoviedb.org/movie/949");
    public static readonly Uri HeatPoster = new("https://image.tmdb.org/t/p/w500/heat.jpg");
    public static readonly Uri DarkPage = new("https://www.themoviedb.org/tv/70523");
    public static readonly Uri DarkPoster = new("https://image.tmdb.org/t/p/w500/dark.jpg");

    /// <summary>Both titles found, with their posters.</summary>
    public static void Answer(ScriptedSilo silo)
    {
        silo.On(HttpMethod.Get, HeatItemPath, ScriptedSilo.Json(HttpStatusCode.OK, HeatItem));
        silo.On(HttpMethod.Get, DarkItemPath, ScriptedSilo.Json(HttpStatusCode.OK, DarkItem));
        silo.On(HttpMethod.Get, HeatDetailPath, ScriptedSilo.Json(HttpStatusCode.OK, HeatDetail));
        silo.On(HttpMethod.Get, DarkDetailPath, ScriptedSilo.Json(HttpStatusCode.OK, DarkDetail));
    }
}

/// <summary>A playing title's TMDB page and poster, read through the server so Granary needs no TMDB key of its own.</summary>
public sealed class TmdbTitleLookupTests
{
    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
    private readonly LogSink _logs = new();

    public TmdbTitleLookupTests() => TmdbPayloads.Answer(_silo);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private TmdbTitleLookup Create() => new(new CatalogApi(_silo.Http), new RequestsApi(_silo.Http), _session, _logs.For<TmdbTitleLookup>());

    private int Asked(string path) => _silo.SentTo(HttpMethod.Get, path).Count;

    [Fact]
    public async Task AMovie_IsFoundByItsOwnItem_WithItsTmdbPageAndPoster()
    {
        var title = await Create().FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token);

        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, TmdbPayloads.HeatPoster), title);
        Assert.Equal((1, 1), (Asked(TmdbPayloads.HeatItemPath), Asked(TmdbPayloads.HeatDetailPath)));
        Assert.All(_silo.Sent, sent => Assert.Equal(SiloAuth.Profile, sent.Auth));
    }

    [Fact]
    public async Task AnEpisode_IsFoundByItsSeries_AsATvShowOnTmdb()
    {
        var title = await Create().FindAsync(PlaybackMediaKind.Episode, TmdbPayloads.DarkId, Token);

        Assert.Equal(new TmdbTitle(TmdbPayloads.DarkPage, TmdbPayloads.DarkPoster), title);
        Assert.Equal((1, 1), (Asked(TmdbPayloads.DarkItemPath), Asked(TmdbPayloads.DarkDetailPath)));
    }

    [Theory]
    [InlineData(409, "capability_disabled")]
    [InlineData(409, "capability_not_configured")]
    [InlineData(403, ProblemCodes.PermissionDenied)]
    [InlineData(404, ProblemCodes.NotFound)]
    public async Task WithRequestsOff_ThereIsNoPoster_ButTheTmdbPageStays(int status, string code)
    {
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatDetailPath, ScriptedSilo.Problem(status, code));

        var title = await Create().FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token);

        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, null), title);
    }

    [Fact]
    public async Task ATitleWithoutATmdbId_HasNothing_AndRequestsAreNotAsked()
    {
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatItemPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "content_id": "movie-heat", "type": "movie", "title": "Heat" }"""));

        Assert.Null(await Create().FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        Assert.Equal(0, Asked(TmdbPayloads.HeatDetailPath));
    }

    [Theory]
    [InlineData("tt0113277")]
    [InlineData("0")]
    [InlineData("-949")]
    [InlineData(" 949")]
    [InlineData("949/../1")]
    public async Task ATmdbIdThatIsNotOne_IsNone(string tmdbId)
    {
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatItemPath,
            ScriptedSilo.Json(HttpStatusCode.OK, $$"""{ "content_id": "movie-heat", "type": "movie", "title": "Heat", "tmdb_id": "{{tmdbId}}" }"""));

        Assert.Null(await Create().FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        Assert.Single(_silo.Sent);
    }

    [Fact]
    public async Task WhatWasFound_IsKeptForTheRun_AndSoIsNone()
    {
        _silo.On(HttpMethod.Get, TmdbPayloads.DarkDetailPath, ScriptedSilo.Problem(409, "capability_disabled"));
        var lookup = Create();

        await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token);
        await lookup.FindAsync(PlaybackMediaKind.Episode, TmdbPayloads.DarkId, Token);
        var heat = await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token);
        var dark = await lookup.FindAsync(PlaybackMediaKind.Episode, TmdbPayloads.DarkId, Token);

        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, TmdbPayloads.HeatPoster), heat);
        Assert.Equal(new TmdbTitle(TmdbPayloads.DarkPage, null), dark);
        Assert.Equal(4, _silo.Sent.Count);
    }

    [Fact]
    public async Task AnItemTheServerDoesNotHave_IsNone_ForTheRunToo()
    {
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatItemPath, ScriptedSilo.Problem(404, ProblemCodes.NotFound));
        var lookup = Create();

        Assert.Null(await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        Assert.Null(await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));

        Assert.Single(_silo.Sent);
    }

    [Fact]
    public async Task AServerThatCouldNotBeReached_IsAskedAgainNextTime()
    {
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatItemPath, ScriptedSilo.NetworkError(), ScriptedSilo.Json(HttpStatusCode.OK, TmdbPayloads.HeatItem));
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatDetailPath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable),
            ScriptedSilo.Json(HttpStatusCode.OK, TmdbPayloads.HeatDetail));
        var lookup = Create();

        Assert.Null(await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, null), await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, TmdbPayloads.HeatPoster), await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, TmdbPayloads.HeatPoster), await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));

        Assert.Equal((3, 2), (Asked(TmdbPayloads.HeatItemPath), Asked(TmdbPayloads.HeatDetailPath)));
        Assert.DoesNotContain(_logs.Lines, line => line.Contains("nas.example", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://nas.example/silo/api/v2/artwork/heat.jpg")]
    [InlineData("//nas.example/heat.jpg")]
    [InlineData("/../../api/v2/artwork/heat.jpg")]
    [InlineData("/heat.jpg?sig=x")]
    public async Task APosterThatIsNotOneOfTmdbs_IsNoPoster(string path)
    {
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatDetailPath,
            ScriptedSilo.Json(HttpStatusCode.OK, $$"""{ "tmdb_id": 949, "media_type": "movie", "title": "Heat", "poster_path": "{{path}}" }"""));

        var title = await Create().FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token);

        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, null), title);
    }

    [Fact]
    public async Task EachProfile_HasItsOwnAnswers_SoOnesRefusalLeavesAnothersPosterAlone()
    {
        // A profile that may not see the title (a child's, say) is refused; another profile of the account is not.
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatDetailPath, ScriptedSilo.Problem(404, ProblemCodes.NotFound),
            ScriptedSilo.Json(HttpStatusCode.OK, TmdbPayloads.HeatDetail));
        var lookup = Create();

        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, null), await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        _session.ProfileId = "8";
        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, TmdbPayloads.HeatPoster), await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        _session.ProfileId = "7";
        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, null), await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));

        Assert.Equal((2, 2), (Asked(TmdbPayloads.HeatItemPath), Asked(TmdbPayloads.HeatDetailPath)));
    }

    [Fact]
    public async Task AnAnswerReadWhileTheProfileChanged_IsDropped_AndNotKeptForEither()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.On(HttpMethod.Get, TmdbPayloads.HeatDetailPath, async () =>
        {
            await answer.Task;
            return await ScriptedSilo.Json(HttpStatusCode.OK, TmdbPayloads.HeatDetail)();
        }, ScriptedSilo.Json(HttpStatusCode.OK, TmdbPayloads.HeatDetail));
        var lookup = Create();

        var first = lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token);
        await ServerPlaybackFixture.Until(() => Asked(TmdbPayloads.HeatDetailPath) == 1, "the poster's request");
        _session.ProfileId = "8";
        answer.SetResult();

        Assert.Null(await first);
        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, TmdbPayloads.HeatPoster), await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        _session.ProfileId = "7";
        Assert.Equal(new TmdbTitle(TmdbPayloads.HeatPage, TmdbPayloads.HeatPoster), await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        Assert.Equal((3, 3), (Asked(TmdbPayloads.HeatItemPath), Asked(TmdbPayloads.HeatDetailPath)));
    }

    [Fact]
    public async Task EachServer_HasItsOwnAnswers()
    {
        var other = ScriptedSilo.Entry("srv2", new Uri("https://other.example/"));
        _silo.On(HttpMethod.Get, ScriptedSilo.Url(TmdbPayloads.HeatItemPath, other.BaseUri),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "content_id": "movie-heat", "type": "movie", "title": "Heat" }"""));
        var lookup = Create();

        Assert.NotNull(await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        _session.ActiveServer = other;

        Assert.Null(await lookup.FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        Assert.Equal(other.BaseUri.Host, _silo.Sent[^1].Uri.Host);
    }

    [Fact]
    public async Task WithoutAServer_NothingIsAsked()
    {
        _session.ActiveServer = null;

        Assert.Null(await Create().FindAsync(PlaybackMediaKind.Movie, TmdbPayloads.HeatId, Token));
        Assert.Empty(_silo.Sent);
    }

    [Theory]
    [InlineData(PlaybackMediaKind.Other)]
    [InlineData(PlaybackMediaKind.Direct)]
    public async Task AnotherKindOfTitle_IsNotLookedUp(PlaybackMediaKind kind)
    {
        Assert.Null(await Create().FindAsync(kind, TmdbPayloads.HeatId, Token));
        Assert.Empty(_silo.Sent);
    }
}
