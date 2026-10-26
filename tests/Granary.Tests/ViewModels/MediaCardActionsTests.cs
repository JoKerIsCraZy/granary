using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.ViewModels;

/// <summary>A card's own buttons, as on Silo's web cards: play, watched, favourite, watchlist.</summary>
public sealed class MediaCardActionsTests
{
    private static readonly MediaCard Lupin = new("series-tvdb-375921", CatalogItemTypes.Series, "Lupin") { PlayContentId = "episode-tvdb-375921-1-1" };

    private readonly ScriptedSilo _silo = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly LogSink _logs = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
    private readonly List<string> _changed = [];
    private readonly ViewerStateChanges _viewerState = new(new PlaybackStops());

    private MediaCardActions Create()
    {
        var actions = new MediaCardActions(new CatalogApi(_silo.Http), _session, _navigation, _logs.For<MediaCardActions>(), _viewerState);
        actions.Changed += (_, id) => _changed.Add(id);
        return actions;
    }

    private const string TitleWatchlistPath = "api/v2/watchlist/titles/movie/603";

    /// <summary>A title from TMDB, as the Requests pages show it.</summary>
    private static readonly MediaCard Matrix = RequestCards.Of(new RequestMediaResult
    {
        TmdbId = 603,
        MediaType = "movie",
        Title = "The Matrix",
        Request = new RequestMediaState { Requestable = true },
    });

    private async Task<MediaCardActions> WithTitleWatchlistAsync(string status = """{ "requests_enabled": true, "watchlist_titles_supported": true }""")
    {
        _silo.On(HttpMethod.Get, "api/v2/requests/status", ScriptedSilo.Json(HttpStatusCode.OK, status));
        var requests = new RequestsApi(_silo.Http);
        var feature = new RequestsFeature(_session, requests, new ManualTimeProvider(), _logs.For<RequestsFeature>());
        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        var actions = new MediaCardActions(new CatalogApi(_silo.Http), _session, _navigation, _logs.For<MediaCardActions>(), _viewerState, requests,
            feature);
        actions.Changed += (_, id) => _changed.Add(id);
        return actions;
    }

    [Fact]
    public async Task ATitleFromTmdb_GoesOnTheWatchlist_AndShowsTheRequestItMade()
    {
        _silo.On(HttpMethod.Put, TitleWatchlistPath, ScriptedSilo.Json(HttpStatusCode.OK, """
            { "request": { "requestable": false, "reason": "already_requested", "status": "pending", "state": "pending", "following": false,
              "requested_by_viewer": true } }
            """));
        var actions = await WithTitleWatchlistAsync();
        Assert.Equal("tmdb:movie:603", Matrix.Id);
        Assert.True(actions.CanToggleTitleWatchlist(Matrix));

        Assert.True(await actions.ToggleTitleWatchlistAsync(Matrix));

        var state = actions.StateOf(Matrix);
        Assert.True(state.InWatchlist);
        Assert.Equal("PENDING", state.StatusBadge);
        Assert.Single(_silo.SentTo(HttpMethod.Put, TitleWatchlistPath));
        Assert.False(actions.IsSending(Matrix));
        Assert.Equal(1, _viewerState.Version);
    }

    [Fact]
    public async Task ATitleOnTheWatchlist_ComesOff()
    {
        _silo.On(HttpMethod.Delete, TitleWatchlistPath, ScriptedSilo.NoContent());
        var actions = await WithTitleWatchlistAsync();

        Assert.True(await actions.ToggleTitleWatchlistAsync(Matrix with { InWatchlist = true }));

        Assert.False(actions.StateOf(Matrix).InWatchlist);
        Assert.Single(_silo.SentTo(HttpMethod.Delete, TitleWatchlistPath));
    }

    [Fact]
    public async Task AWatchlistAddTheServerRefuses_GoesBack()
    {
        _silo.On(HttpMethod.Put, TitleWatchlistPath, ScriptedSilo.Problem(502, "bad_gateway"));
        var actions = await WithTitleWatchlistAsync();

        Assert.False(await actions.ToggleTitleWatchlistAsync(Matrix));

        Assert.False(actions.StateOf(Matrix).InWatchlist);
        Assert.Null(actions.StateOf(Matrix).StatusBadge);
    }

    [Fact]
    public async Task AWatchlistChangeOnItsWay_IsNotSentTwice()
    {
        var release = new TaskCompletionSource();
        _silo.On(HttpMethod.Put, TitleWatchlistPath, async () =>
        {
            await release.Task;
            return await ScriptedSilo.Json(HttpStatusCode.OK, """{ "request": { "requestable": true } }""")();
        });
        var actions = await WithTitleWatchlistAsync();

        var first = actions.ToggleTitleWatchlistAsync(Matrix);
        Assert.True(actions.IsSending(Matrix));
        Assert.False(await actions.ToggleTitleWatchlistAsync(Matrix));
        release.SetResult();

        Assert.True(await first);
        Assert.Single(_silo.SentTo(HttpMethod.Put, TitleWatchlistPath));
        Assert.True(actions.StateOf(Matrix).InWatchlist);
        Assert.Null(actions.StateOf(Matrix).StatusBadge);
    }

    [Fact]
    public async Task ARemoveTheServerRefuses_LeavesTheTitleOnTheWatchlist()
    {
        _silo.On(HttpMethod.Delete, TitleWatchlistPath, ScriptedSilo.NetworkError());
        var actions = await WithTitleWatchlistAsync();
        var onIt = Matrix with { InWatchlist = true };

        Assert.False(await actions.ToggleTitleWatchlistAsync(onIt));

        Assert.True(actions.StateOf(onIt).InWatchlist);
    }

    [Fact]
    public async Task AnAnswerAfterAnotherProfileTookOver_ChangesNothingOfIt()
    {
        var release = new TaskCompletionSource();
        _silo.On(HttpMethod.Put, TitleWatchlistPath, async () =>
        {
            await release.Task;
            return await ScriptedSilo.Json(HttpStatusCode.OK, """{ "request": { "requestable": false, "status": "pending", "state": "pending" } }""")();
        });
        var actions = await WithTitleWatchlistAsync();

        var toggle = actions.ToggleTitleWatchlistAsync(Matrix);
        _session.ProfileId = "8";
        _session.RaiseChanged();
        release.SetResult();
        await toggle;

        var state = actions.StateOf(Matrix);
        Assert.False(state.InWatchlist);
        Assert.Null(state.StatusBadge);
        Assert.Equal(0, _viewerState.Version);
        Assert.False(actions.IsSending(Matrix));
    }

    [Fact]
    public async Task TheRequestItMade_GivesWayToANewerAnswer()
    {
        _silo.On(HttpMethod.Put, TitleWatchlistPath, ScriptedSilo.Json(HttpStatusCode.OK, """
            { "request": { "requestable": false, "status": "pending", "state": "pending" } }
            """));
        var actions = await WithTitleWatchlistAsync();
        await actions.ToggleTitleWatchlistAsync(Matrix);
        Assert.Equal("PENDING", actions.StatusBadgeOf(Matrix));

        // The page read the title again: the request was approved meanwhile.
        Assert.Equal("APPROVED", actions.StatusBadgeOf(Matrix with { StatusBadge = "APPROVED" }));
    }

    [Theory]
    [InlineData("""{ "requests_enabled": true, "watchlist_titles_supported": false }""")]
    [InlineData("""{ "requests_enabled": false, "watchlist_titles_supported": true }""")]
    public async Task AServerWithoutAWatchlistOfTmdbTitles_OffersNone(string status)
    {
        var actions = await WithTitleWatchlistAsync(status);

        Assert.False(actions.CanToggleTitleWatchlist(Matrix));
        Assert.False(await actions.ToggleTitleWatchlistAsync(Matrix));
        Assert.Empty(_silo.SentTo(HttpMethod.Put, TitleWatchlistPath));
    }

    [Fact]
    public async Task ALibraryCard_HasNoTmdbWatchlist()
    {
        var actions = await WithTitleWatchlistAsync();

        Assert.False(actions.CanToggleTitleWatchlist(Lupin));
        Assert.False(actions.CanToggleTitleWatchlist(Matrix with { Id = "movie:603" }));
    }

    [Theory]
    [InlineData("tmdb:movie:603", "movie", 603L)]
    [InlineData("tmdb:series:1399", "series", 1399L)]
    [InlineData("tmdb:person:1", null, 0L)]
    [InlineData("tmdb:movie:-3", null, 0L)]
    [InlineData("tmdb:movie:1:2", null, 0L)]
    [InlineData("movie:603", null, 0L)]
    public void ACardsId_NamesItsTmdbTitle(string id, string? mediaType, long tmdbId) =>
        Assert.Equal(mediaType is null ? null : (mediaType, tmdbId), RequestCards.TitleOf(id));

    [Fact]
    public void Play_StartsTheNextEpisode_WhereTheViewerLeftOff()
    {
        var actions = Create();

        actions.Play(Lupin);

        var (route, parameter) = Assert.Single(_navigation.Navigations);
        Assert.Equal(AppRoute.Player, route);
        Assert.Equal(PlayRequest.Resume("episode-tvdb-375921-1-1"), parameter);
    }

    [Fact]
    public void ACardWithoutAPlayTarget_StartsNothing()
    {
        var actions = Create();

        actions.Play(Lupin with { PlayContentId = null });

        Assert.Empty(_navigation.Navigations);
        Assert.False(MediaCardActions.CanPlay(Lupin with { PlayContentId = null }));
    }

    [Fact]
    public async Task MarkWatched_ShowsAtOnce_AndTellsTheServer()
    {
        _silo.On(HttpMethod.Post, "api/v2/watched/series-tvdb-375921", ScriptedSilo.NoContent());
        var actions = Create();

        Assert.True(await actions.ToggleWatchedAsync(Lupin));

        Assert.True(actions.StateOf(Lupin).IsWatched);
        Assert.Equal(["series-tvdb-375921"], _changed);
        Assert.Single(_silo.SentTo(HttpMethod.Post, "api/v2/watched/series-tvdb-375921"));
        // Home's Continue Watching and Next Up are stale after it.
        Assert.Equal(1, _viewerState.Version);
    }

    [Fact]
    public async Task AChangeTheServerRefuses_GoesBack()
    {
        _silo.On(HttpMethod.Put, "api/v2/favorites/series-tvdb-375921", ScriptedSilo.Problem(500, "internal"));
        var actions = Create();

        Assert.False(await actions.ToggleFavoriteAsync(Lupin));

        Assert.False(actions.StateOf(Lupin).IsFavorite);
        Assert.Equal(2, _changed.Count);
        Assert.Equal(0, _viewerState.Version);
    }

    [Fact]
    public async Task Toggles_TurnTheStateTheCardHad()
    {
        _silo.On(HttpMethod.Delete, "api/v2/favorites/series-tvdb-375921", ScriptedSilo.NoContent());
        _silo.On(HttpMethod.Put, "api/v2/watchlist/series-tvdb-375921", ScriptedSilo.NoContent());
        var actions = Create();
        var favourite = Lupin with { IsFavorite = true };

        await actions.ToggleFavoriteAsync(favourite);
        await actions.ToggleWatchlistAsync(favourite);

        Assert.Equal(new MediaCardState(false, false, true, false), actions.StateOf(favourite));
        // The same title on another card, a row further down or a recycled container, shows the change too.
        Assert.Equal(new MediaCardState(false, false, true), actions.StateOf(Lupin with { Title = "Lupin (2021)" }));
    }

    [Fact]
    public async Task AnotherSession_ShowsTheServersStateAgain()
    {
        _silo.On(HttpMethod.Post, "api/v2/watched/series-tvdb-375921", ScriptedSilo.NoContent());
        var actions = Create();
        await actions.ToggleWatchedAsync(Lupin);

        _session.RaiseChanged();

        Assert.False(actions.StateOf(Lupin).IsWatched);
    }

    [Theory]
    [InlineData(CatalogItemTypes.Movie, true, true)]
    [InlineData(CatalogItemTypes.Series, true, true)]
    [InlineData(CatalogItemTypes.Episode, true, false)]
    [InlineData("person", false, false)]
    public void WhatACardOffers_DependsOnItsType(string type, bool watched, bool collect)
    {
        var card = new MediaCard("id", type, "Title");

        Assert.Equal(watched, MediaCardActions.CanMarkWatched(card));
        Assert.Equal(collect, MediaCardActions.CanCollect(card));
    }

    private static readonly MediaCard Heat = new("movie-heat", CatalogItemTypes.Movie, "Heat") { Progress = 0.4, PlayContentId = "movie-heat" };

    [Fact]
    public void PlayFromBeginning_StartsAtZero_NotWhereTheViewerLeftOff()
    {
        var actions = Create();

        actions.PlayFromBeginning(Heat);

        Assert.Equal((AppRoute.Player, (object?)PlayRequest.FromStart("movie-heat")), Assert.Single(_navigation.Navigations));
    }

    [Fact]
    public void TheWebsMenuRules_AreTheCardsToo()
    {
        var actions = Create();
        var fresh = Heat with { Progress = null };

        Assert.True(MediaCardActions.CanPlayFromBeginning(Heat, actions.StateOf(Heat)));
        Assert.True(MediaCardActions.CanResetProgress(Heat, actions.StateOf(Heat)));
        Assert.False(MediaCardActions.CanPlayFromBeginning(fresh, actions.StateOf(fresh)));
        Assert.True(MediaCardActions.CanPlayFromBeginning(fresh with { IsWatched = true }, actions.StateOf(fresh with { IsWatched = true })));
        Assert.False(MediaCardActions.CanResetProgress(fresh, actions.StateOf(fresh)));
        // A series plays its next episode; Not interested takes movies and series, never episodes.
        Assert.False(MediaCardActions.CanPlayFromBeginning(Lupin with { Progress = 0.5 }, actions.StateOf(Lupin)));
        Assert.True(MediaCardActions.CanMarkNotInterested(Lupin));
        Assert.False(MediaCardActions.CanMarkNotInterested(new MediaCard("e1", CatalogItemTypes.Episode, "Pilot")));
    }

    [Fact]
    public async Task ResetProgress_ClearsTheResumePoint_AndTheBarGoes()
    {
        _silo.On(HttpMethod.Delete, "api/v2/progress/movie-heat/position", ScriptedSilo.NoContent());
        var actions = Create();

        Assert.True(await actions.ResetProgressAsync(Heat));

        Assert.Single(_silo.SentTo(HttpMethod.Delete, "api/v2/progress/movie-heat/position"));
        Assert.True(actions.StateOf(Heat).IsProgressCleared);
        Assert.False(MediaCardActions.CanResetProgress(Heat, actions.StateOf(Heat)));
        Assert.Equal(1, _viewerState.Version);
    }

    [Fact]
    public async Task NotInterested_MarksAndUnmarks()
    {
        _silo.On(HttpMethod.Put, "api/v2/not-interested/series-tvdb-375921", ScriptedSilo.NoContent());
        _silo.On(HttpMethod.Delete, "api/v2/not-interested/series-tvdb-375921", ScriptedSilo.NoContent());
        var actions = Create();

        await actions.ToggleNotInterestedAsync(Lupin);
        Assert.True(actions.StateOf(Lupin).IsNotInterested);
        await actions.ToggleNotInterestedAsync(Lupin);

        Assert.False(actions.StateOf(Lupin).IsNotInterested);
        Assert.Single(_silo.SentTo(HttpMethod.Put, "api/v2/not-interested/series-tvdb-375921"));
        Assert.Single(_silo.SentTo(HttpMethod.Delete, "api/v2/not-interested/series-tvdb-375921"));
    }
}
