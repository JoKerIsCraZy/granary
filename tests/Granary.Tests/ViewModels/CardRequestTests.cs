using System.Net;
using System.Text.Json;
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

/// <summary>The Request action in the middle of a TMDB title's card, as the web's <c>RequestPosterCard</c> offers it on hover.</summary>
public sealed class CardRequestTests
{
    private const string CreatePath = "api/v2/requests";

    private const string Pending = """
        { "id": "r9", "media_type": "movie", "tmdb_id": 603, "title": "The Matrix", "status": "pending", "outcome": "active", "state": "pending",
          "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-10-29T12:00:00Z", "updated_at": "2026-10-29T12:00:00Z" }
        """;

    private static readonly MediaCard Matrix = RequestCards.Of(new RequestMediaResult
    {
        TmdbId = 603,
        MediaType = "movie",
        Title = "The Matrix",
        Year = 1999,
        Overview = "A hacker learns what the world is.",
        PosterPath = "/matrix.jpg",
        BackdropPath = "/matrix-wide.jpg",
        Request = new RequestMediaState { Requestable = true },
    });

    private static readonly MediaCard Dune = RequestCards.Of(new RequestMediaResult
    {
        TmdbId = 438631,
        MediaType = "movie",
        Title = "Dune",
        Year = 2021,
        Request = new RequestMediaState { Requestable = true },
    });

    private static readonly MediaCard Severance = RequestCards.Of(new RequestMediaResult
    {
        TmdbId = 95396,
        MediaType = "series",
        Title = "Severance",
        Year = 2022,
        Request = new RequestMediaState { Requestable = true },
    });

    private readonly ScriptedSilo _silo = new();
    private readonly LogSink _logs = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
    private readonly List<CardRequestAnswer> _answers = [];

    private async Task<MediaCardActions> CreateAsync(string status = """{ "requests_enabled": true, "watchlist_titles_supported": true }""")
    {
        _silo.On(HttpMethod.Get, "api/v2/requests/status", ScriptedSilo.Json(HttpStatusCode.OK, status));
        var requests = new RequestsApi(_silo.Http);
        var feature = new RequestsFeature(_session, requests, new ManualTimeProvider(), _logs.For<RequestsFeature>());
        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        var actions = new MediaCardActions(new CatalogApi(_silo.Http), _session, new RecordingNavigation(), _logs.For<MediaCardActions>(),
            new ViewerStateChanges(new PlaybackStops()), requests, feature);
        actions.RequestAnswered += (_, answer) => _answers.Add(answer);
        return actions;
    }

    private JsonElement SentBody() => JsonDocument.Parse(Assert.Single(_silo.SentTo(HttpMethod.Post, CreatePath)).Body!).RootElement;

    [Fact]
    public async Task ARequestableTitle_OffersRequest_InTheWebsWords()
    {
        var actions = await CreateAsync();

        Assert.True(actions.CanRequest(Matrix));
        Assert.False(actions.IsRequesting(Matrix));
        Assert.Equal("Request", actions.RequestActionText(Matrix));
        Assert.Equal("Request The Matrix (Movie · 1999)", actions.RequestActionName(Matrix));
        Assert.Equal("Request Severance (Series · 2022)", actions.RequestActionName(Severance));
    }

    [Theory]
    [InlineData("already_requested")]
    [InlineData("already_available")]
    [InlineData("quota_exceeded")]
    [InlineData("blocked")]
    public async Task ATitleTheViewerCannotRequest_OffersNoRequest(string reason)
    {
        var actions = await CreateAsync();
        var card = RequestCards.Of(new RequestMediaResult
        {
            TmdbId = 11,
            MediaType = "movie",
            Title = "Asked For",
            Request = new RequestMediaState { Requestable = false, Reason = reason },
        });

        Assert.Null(card.RequestOffer);
        Assert.False(actions.CanRequest(card));
        Assert.False(await actions.RequestAsync(card));
        Assert.Empty(_silo.SentTo(HttpMethod.Post, CreatePath));
    }

    [Fact]
    public async Task AServerThatTakesNoRequests_OffersNone()
    {
        var actions = await CreateAsync("""{ "requests_enabled": false, "watchlist_titles_supported": false }""");

        Assert.False(actions.CanRequest(Matrix));
        Assert.False(await actions.RequestAsync(Matrix));
        Assert.Empty(_silo.SentTo(HttpMethod.Post, CreatePath));
    }

    [Fact]
    public async Task ALibraryCard_OffersNoRequest()
    {
        var actions = await CreateAsync();
        var library = new MediaCard("movie-heat", CatalogItemTypes.Movie, "Heat") { RequestOffer = Matrix.RequestOffer };

        Assert.False(actions.CanRequest(library));
    }

    [Fact]
    public async Task Request_SendsTheMovie_AndTheCardShowsTheRequestsState()
    {
        _silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Json(HttpStatusCode.Created, Pending));
        var actions = await CreateAsync();

        Assert.True(await actions.RequestAsync(Matrix));

        var body = SentBody();
        Assert.Equal("movie", body.GetProperty("media_type").GetString());
        Assert.Equal(603, body.GetProperty("tmdb_id").GetInt64());
        Assert.Equal("The Matrix", body.GetProperty("title").GetString());
        Assert.Equal(1999, body.GetProperty("year").GetInt32());
        Assert.Equal("A hacker learns what the world is.", body.GetProperty("overview").GetString());
        Assert.Equal("/matrix.jpg", body.GetProperty("poster_path").GetString());
        Assert.Equal("/matrix-wide.jpg", body.GetProperty("backdrop_path").GetString());
        Assert.Equal("PENDING", actions.StatusBadgeOf(Matrix));
        Assert.False(actions.CanRequest(Matrix));
        Assert.False(actions.IsRequesting(Matrix));
        var answer = Assert.Single(_answers);
        Assert.True(answer.Succeeded);
        Assert.Equal("Request submitted", answer.Text);
    }

    // The web's hover action sends requestInputFromMediaResult as it is: a series without seasons, so all of it. Only its
    // title page asks which seasons.
    [Fact]
    public async Task ASeries_IsRequestedWhole_AsTheWebsCardDoes()
    {
        _silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Json(HttpStatusCode.Created, Pending));
        var actions = await CreateAsync();

        Assert.True(await actions.RequestAsync(Severance));

        var body = SentBody();
        Assert.Equal("series", body.GetProperty("media_type").GetString());
        Assert.Equal(95396, body.GetProperty("tmdb_id").GetInt64());
        Assert.False(body.TryGetProperty("seasons", out var seasons) && seasons.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task WhileSending_TheCardSaysSending_AndTheRequestIsNotSentTwice()
    {
        var release = new TaskCompletionSource();
        _silo.On(HttpMethod.Post, CreatePath, async () =>
        {
            await release.Task;
            return await ScriptedSilo.Json(HttpStatusCode.Created, Pending)();
        });
        var actions = await CreateAsync();

        var first = actions.RequestAsync(Matrix);
        Assert.True(actions.IsRequesting(Matrix));
        Assert.Equal("Sending", actions.RequestActionText(Matrix));
        Assert.Equal("Sending request for The Matrix (Movie · 1999)", actions.RequestActionName(Matrix));
        Assert.False(await actions.RequestAsync(Matrix));
        release.SetResult();

        Assert.True(await first);
        Assert.Single(_silo.SentTo(HttpMethod.Post, CreatePath));
    }

    [Fact]
    public async Task ARequestTheServerRefuses_GoesBackToRequest()
    {
        _silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Problem(502, "bad_gateway"));
        var actions = await CreateAsync();

        Assert.False(await actions.RequestAsync(Matrix));

        Assert.True(actions.CanRequest(Matrix));
        Assert.False(actions.IsRequesting(Matrix));
        Assert.Equal("Request", actions.RequestActionText(Matrix));
        Assert.Null(actions.StatusBadgeOf(Matrix));
        var answer = Assert.Single(_answers);
        Assert.False(answer.Succeeded);
        Assert.Equal("Failed to submit request", answer.Text);
    }

    [Fact]
    public async Task ALostAnswer_IsNotSentAgain()
    {
        _silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.NetworkError());
        var actions = await CreateAsync();

        Assert.False(await actions.RequestAsync(Matrix));

        Assert.Single(_silo.SentTo(HttpMethod.Post, CreatePath));
    }

    // A recycled card view shows another title: what one title's request did belongs to it alone.
    [Fact]
    public async Task AnotherTitle_KeepsItsOwnState_WhileOneIsRequested()
    {
        var release = new TaskCompletionSource();
        _silo.On(HttpMethod.Post, CreatePath, async () =>
        {
            await release.Task;
            return await ScriptedSilo.Json(HttpStatusCode.Created, Pending)();
        });
        var actions = await CreateAsync();

        var request = actions.RequestAsync(Matrix);
        Assert.False(actions.IsRequesting(Dune));
        Assert.Equal("Request", actions.RequestActionText(Dune));
        release.SetResult();
        await request;

        Assert.True(actions.CanRequest(Dune));
        Assert.Null(actions.StatusBadgeOf(Dune));
        Assert.False(actions.CanRequest(Matrix));
    }

    [Fact]
    public async Task AnotherProfile_SeesNoSending_AndMayRequestTheTitleItself()
    {
        var release = new TaskCompletionSource();
        _silo.On(HttpMethod.Post, CreatePath, async () =>
        {
            await release.Task;
            return await ScriptedSilo.Json(HttpStatusCode.Created, Pending)();
        });
        var actions = await CreateAsync();

        var request = actions.RequestAsync(Matrix);
        Assert.True(actions.IsRequesting(Matrix));
        _session.ProfileId = "8";
        _session.RaiseChanged();

        Assert.False(actions.IsRequesting(Matrix));
        Assert.False(actions.HasRequestsInFlight);
        Assert.True(actions.CanRequest(Matrix));
        Assert.Equal("Request", actions.RequestActionText(Matrix));
        release.SetResult();
        await request;
        Assert.False(actions.IsRequesting(Matrix));
        Assert.True(actions.CanRequest(Matrix));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("declined")]
    public async Task ATitleReadAgain_AfterItsRequestEnded_OffersRequestAgain(string ended)
    {
        _silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Json(HttpStatusCode.Created, Pending));
        var actions = await CreateAsync();
        await actions.RequestAsync(Matrix);
        Assert.False(actions.CanRequest(Matrix));

        var readAgain = RequestCards.Of(new RequestMediaResult
        {
            TmdbId = 603,
            MediaType = "movie",
            Title = "The Matrix",
            Year = 1999,
            Request = new RequestMediaState { Requestable = true, Status = "pending", State = ended },
        });

        Assert.Equal(Matrix.Id, readAgain.Id);
        Assert.True(actions.CanRequest(readAgain));
        Assert.Equal(ended.ToUpperInvariant(), actions.StatusBadgeOf(readAgain));
    }

    [Fact]
    public async Task ATitleReadAgain_AfterItsRequest_SaysWhereItStandsNow()
    {
        _silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Json(HttpStatusCode.Created, Pending));
        var actions = await CreateAsync();
        await actions.RequestAsync(Matrix);

        var approved = RequestCards.Of(new RequestMediaResult
        {
            TmdbId = 603,
            MediaType = "movie",
            Title = "The Matrix",
            Request = new RequestMediaState { Requestable = false, Reason = "already_requested", Status = "approved", State = "approved" },
        });

        Assert.False(actions.CanRequest(approved));
        Assert.Equal("APPROVED", actions.StatusBadgeOf(approved));
    }

    [Fact]
    public async Task AnAlreadyRequestedAnswer_FailsLikeAnyOther_AndOffersRequestAgain()
    {
        _silo.On(HttpMethod.Post, CreatePath, ScriptedSilo.Problem(409, "already_requested"));
        var actions = await CreateAsync();

        Assert.False(await actions.RequestAsync(Matrix));

        Assert.True(actions.CanRequest(Matrix));
        Assert.Null(actions.StatusBadgeOf(Matrix));
        Assert.False(Assert.Single(_answers).Succeeded);
    }

    [Fact]
    public async Task ASurpriseFailure_IsCaught_AndOffersRequestAgain()
    {
        _silo.On(HttpMethod.Post, CreatePath, () => throw new InvalidOperationException("The handler broke."));
        var actions = await CreateAsync();

        Assert.False(await actions.RequestAsync(Matrix));

        Assert.True(actions.CanRequest(Matrix));
        Assert.False(actions.IsRequesting(Matrix));
        Assert.False(Assert.Single(_answers).Succeeded);
    }

    [Fact]
    public void WhatTheCardSends_LeavesOutWhatTmdbLeftBlank()
    {
        var card = RequestCards.Of(new RequestMediaResult
        {
            TmdbId = 7,
            MediaType = "series",
            Title = "Untitled",
            Year = 0,
            Overview = "  ",
            PosterPath = "",
            BackdropPath = " ",
            Request = new RequestMediaState { Requestable = true },
        });

        var offer = Assert.IsType<MediaRequestCreate>(card.RequestOffer);
        Assert.Equal(("series", 7L, "Untitled"), (offer.MediaType, offer.TmdbId, offer.Title));
        Assert.Null(offer.Year);
        Assert.Null(offer.Overview);
        Assert.Null(offer.PosterPath);
        Assert.Null(offer.BackdropPath);
        Assert.Null(offer.Seasons);
    }

    [Theory]
    [InlineData(0L, "movie")]
    [InlineData(-1L, "movie")]
    [InlineData(5L, "person")]
    [InlineData(5L, "")]
    public void ATitleThatIsNotOne_OffersNothingToRequest(long tmdbId, string mediaType)
    {
        var card = RequestCards.Of(new RequestMediaResult
        {
            TmdbId = tmdbId,
            MediaType = mediaType,
            Title = "Odd",
            Request = new RequestMediaState { Requestable = true },
        });

        Assert.Null(card.RequestOffer);
    }

    [Fact]
    public async Task AnAnswerAfterAnotherProfileTookOver_ChangesNothingOfIt()
    {
        var release = new TaskCompletionSource();
        _silo.On(HttpMethod.Post, CreatePath, async () =>
        {
            await release.Task;
            return await ScriptedSilo.Json(HttpStatusCode.Created, Pending)();
        });
        var actions = await CreateAsync();

        var request = actions.RequestAsync(Matrix);
        _session.ProfileId = "8";
        _session.RaiseChanged();
        release.SetResult();
        await request;

        Assert.Null(actions.StatusBadgeOf(Matrix));
        Assert.False(actions.IsRequesting(Matrix));
        Assert.Empty(_answers);
    }
}
