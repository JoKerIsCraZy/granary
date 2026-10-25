using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;
using Granary.Core.Playback;
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
