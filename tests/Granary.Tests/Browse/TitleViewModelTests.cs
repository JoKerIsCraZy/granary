using System.Net;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api.Requests;
using Granary.Core.ViewModels;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>A title from TMDB, as Silo's web title page shows it; the clock stands on 1 October 2026 and the account is "1".</summary>
public sealed class TitleViewModelTests
{
    private const string MoviePath = "api/v2/requests/detail/movie/603";
    private const string SeriesPath = "api/v2/requests/detail/series/1399";
    private const string StatusPath = "api/v2/requests/status";

    private const string Movie = """
        {
          "tmdb_id": 603, "media_type": "movie", "title": "The Matrix", "year": 1999, "tagline": "Welcome to the Real World.",
          "overview": "A hacker learns the truth.", "poster_path": "/matrix.jpg", "backdrop_path": "/matrix-bg.jpg",
          "content_rating": "R", "runtime": 136, "vote_average": 8.2, "vote_count": 25840, "imdb_id": "tt0133093",
          "director": "Lana Wachowski", "creators": [], "genres": ["Action", "Science Fiction"], "networks": [],
          "production_companies": ["Warner Bros. Pictures"], "cast": [
            { "name": "Keanu Reeves", "character": "Neo", "order": 0, "profile_path": "/keanu.jpg" },
            { "name": "Carrie-Anne Moss", "character": "Trinity", "order": 1 }
          ],
          "seasons": [], "recommendations": [
            { "tmdb_id": 604, "media_type": "movie", "title": "The Matrix Reloaded", "year": 2003, "availability": "none", "in_watchlist": false,
              "request": { "requestable": true, "following": false, "requested_by_viewer": false } },
            { "tmdb_id": 605, "media_type": "movie", "title": "The Matrix Revolutions", "year": 2003, "availability": "available",
              "library_content_id": "movie:the-matrix-revolutions-2003", "in_watchlist": false,
              "request": { "requestable": false, "reason": "already_available", "following": false, "requested_by_viewer": false } }
          ],
          "availability": "none", "in_watchlist": false,
          "request": { "following": false, "requestable": true, "requested_by_viewer": false }
        }
        """;

    /// <summary>Season 1 in the library, 2 aired and missing, 3 requested, 4 announced for next year, and specials.</summary>
    private const string Series = """
        {
          "tmdb_id": 1399, "media_type": "series", "title": "Example Show", "first_air_date": "2019-04-01", "last_air_date": "2026-05-01",
          "number_of_seasons": 4, "number_of_episodes": 30, "status": "Returning Series", "creators": ["A. Writer", "B. Writer"],
          "genres": ["Drama"], "networks": ["HBO"], "production_companies": ["Studio"], "cast": [], "recommendations": [],
          "seasons": [
            { "season_number": 0, "name": "Specials", "air_date": "2019-01-01", "episode_count": 2, "availability": "missing", "requested": false },
            { "season_number": 1, "name": "Season 1", "air_date": "2019-04-01", "episode_count": 10, "availability": "available", "requested": false },
            { "season_number": 2, "name": "The Return", "air_date": "2022-04-01", "episode_count": 9, "availability": "missing", "requested": false },
            { "season_number": 3, "air_date": "2026-05-01", "episode_count": 8, "availability": "missing", "requested": true },
            { "season_number": 4, "air_date": "2027-03-01", "episode_count": 1, "availability": "missing", "requested": false }
          ],
          "availability": "partial", "in_watchlist": true,
          "request": { "requestable": true, "following": false, "requested_by_viewer": false }
        }
        """;

    private readonly LibraryHarness _harness = new();
    private readonly RequestsFeature _feature;

    public TitleViewModelTests()
    {
        _harness.Answer(MoviePath, Movie);
        _harness.Answer(SeriesPath, Series);
        _harness.Answer(StatusPath, """{ "requests_enabled": true, "watchlist_titles_supported": true, "watchlist_requests": true }""");
        _harness.Silo.On(HttpMethod.Post, "api/v2/requests", ScriptedSilo.Json(HttpStatusCode.Created, """
            { "id": "r9", "media_type": "movie", "tmdb_id": 603, "title": "The Matrix", "status": "pending", "outcome": "active", "state": "pending",
              "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-10-01T12:00:00Z", "updated_at": "2026-10-01T12:00:00Z",
              "is_anime": false, "provider": "silo", "source": "web" }
            """));
        _feature = new RequestsFeature(_harness.Session, Api(), _harness.Clock, _harness.Logs.For<RequestsFeature>());
    }

    private RequestsApi Api() => new(_harness.Silo.Http);

    /// <summary>The account's own pending request, named by its id.</summary>
    private const string PendingOwn = """
        "request": { "requestable": false, "reason": "already_requested", "status": "pending", "state": "pending", "following": false, "requested_by_viewer": true }
        """;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }

    /// <summary><see cref="Movie"/> with another request block (and anything before it, such as a library id).</summary>
    private static string MovieWith(string request) => Movie.Replace(
        "\"request\": { \"following\": false, \"requestable\": true, \"requested_by_viewer\": false }", request, StringComparison.Ordinal);

    private TitleViewModel Create() => new(_harness.Session, Api(), _feature, _harness.Navigation, _harness.Clock, _harness.Logs.For<TitleViewModel>());

    private async Task<TitleViewModel> ShownAsync(string mediaType = "movie", long tmdbId = 603)
    {
        await _feature.RefreshAsync(TestContext.Current.CancellationToken);
        var title = Create();
        await title.ShowAsync(new TitleRouteParameter(mediaType, tmdbId, "Loading"));
        return title;
    }

    [Fact]
    public async Task AMovie_ShowsLikeALibraryTitle()
    {
        var title = await ShownAsync();

        Assert.Equal(LibraryLoadState.Ready, title.State);
        Assert.Equal("The Matrix", title.Title);
        Assert.Equal("MOVIE · WARNER BROS. PICTURES", title.Overline);
        Assert.Equal(["1999", "R", "2H 16M"], title.Chips.Select(c => c.Text));
        Assert.Equal(("TMDB", "8.2"), (title.Ratings[0].Name, title.Ratings[0].Display));
        Assert.Equal("25.8K votes", title.VotesText);
        Assert.Equal("Directed by Lana Wachowski", title.CrewLine);
        Assert.Equal("Action, Science Fiction", title.Genres);
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/original/matrix-bg.jpg"), title.BackdropUrl);
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w500/matrix.jpg"), title.PosterUrl);
        Assert.Equal(new Uri("https://www.imdb.com/title/tt0133093"), title.ImdbUrl);
        Assert.Equal(new Uri("https://www.themoviedb.org/movie/603"), title.TmdbUrl);
        Assert.Equal(["Keanu Reeves", "Carrie-Anne Moss"], title.Cast.Select(c => c.Name));
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w185/keanu.jpg"), title.Cast[0].PhotoUrl);
        Assert.False(title.HasSeasons);
    }

    [Fact]
    public async Task ARequestableMovie_IsRequestedWithItsDetails()
    {
        var title = await ShownAsync();
        Assert.Equal(TitlePrimaryAction.Request, title.PrimaryAction);
        Assert.Equal("Request movie", title.PrimaryLabel);
        Assert.True(title.AddAlsoRequests);

        await title.RequestCommand.ExecuteAsync(null);

        var body = JsonDocument.Parse(Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, "api/v2/requests")).Body!).RootElement;
        Assert.Equal("movie", body.GetProperty("media_type").GetString());
        Assert.Equal(603, body.GetProperty("tmdb_id").GetInt64());
        Assert.Equal("/matrix.jpg", body.GetProperty("poster_path").GetString());
        Assert.False(body.TryGetProperty("seasons", out _));
        Assert.Equal("Requested The Matrix", title.Notice);
        Assert.Equal(2, _harness.Count(MoviePath));
    }

    [Fact]
    public async Task ARequestThatFails_SaysSo_AndIsNotSentAgain()
    {
        _harness.Silo.On(HttpMethod.Post, "api/v2/requests", ScriptedSilo.Problem(429, "quota_exceeded"));
        var title = await ShownAsync();

        await title.RequestCommand.ExecuteAsync(null);

        Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, "api/v2/requests"));
        Assert.True(title.IsNoticeError);
        Assert.Equal("Couldn't send the request. Try again.", title.Notice);
    }

    [Fact]
    public async Task AViewersOwnPendingRequest_CanBeCancelled()
    {
        _harness.Answer(MoviePath, MovieWith("""
            "request": { "requestable": false, "reason": "already_requested", "status": "pending", "state": "pending", "request_id": "r9", "following": false, "requested_by_viewer": true }
            """));
        _harness.Answer("api/v2/requests/r9", """
            { "id": "r9", "media_type": "movie", "tmdb_id": 603, "title": "The Matrix", "status": "pending", "outcome": "active", "state": "pending",
              "requested_by_user_id": "1", "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-10-01T12:00:00Z",
              "updated_at": "2026-10-01T12:00:00Z", "is_anime": false, "provider": "silo", "source": "web" }
            """);
        _harness.Silo.On(HttpMethod.Post, "api/v2/requests/r9/cancel", ScriptedSilo.Json(HttpStatusCode.OK, """
            { "id": "r9", "media_type": "movie", "tmdb_id": 603, "title": "The Matrix", "status": "pending", "outcome": "cancelled", "state": "cancelled",
              "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-10-01T12:00:00Z", "updated_at": "2026-10-01T12:00:00Z",
              "is_anime": false, "provider": "silo", "source": "web" }
            """));

        var title = await ShownAsync();
        Assert.Equal("Requested", title.PrimaryLabel);
        Assert.False(title.IsPrimaryEnabled);
        Assert.True(title.CanCancel);
        Assert.False(title.CanFollow);

        await title.CancelCommand.ExecuteAsync(null);
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, "api/v2/requests/r9/cancel"));
    }

    [Fact]
    public async Task AnotherAccountsRequest_CannotBeCancelled_ButFollowed()
    {
        _harness.Answer(MoviePath, MovieWith("""
            "request": { "requestable": false, "reason": "already_requested", "status": "approved", "state": "approved", "request_id": "r7", "following": false, "requested_by_viewer": false }
            """));
        _harness.Answer("api/v2/requests/r7", """
            { "id": "r7", "media_type": "movie", "tmdb_id": 603, "title": "The Matrix", "status": "approved", "outcome": "active", "state": "approved",
              "requested_by_user_id": "2", "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-10-01T12:00:00Z",
              "updated_at": "2026-10-01T12:00:00Z", "is_anime": false, "provider": "silo", "source": "web" }
            """);
        _harness.Silo.On(HttpMethod.Put, "api/v2/requests/follows/movie/603", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "requestable": false, "following": true, "requested_by_viewer": false }"""));

        var title = await ShownAsync();
        Assert.Equal("Approved", title.PrimaryLabel);
        Assert.False(title.CanCancel);
        Assert.True(title.CanFollow);
        Assert.Equal("Notify me when available", title.FollowLabel);

        await title.ToggleFollowCommand.ExecuteAsync(null);
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, "api/v2/requests/follows/movie/603"));
    }

    [Fact]
    public async Task ATitleInTheLibrary_OpensThere()
    {
        _harness.Answer(MoviePath, MovieWith("""
            "library_content_id": "movie:the-matrix-1999",
            "request": { "requestable": false, "reason": "already_available", "following": false, "requested_by_viewer": false }
            """));

        var title = await ShownAsync();
        Assert.Equal(TitlePrimaryAction.OpenInLibrary, title.PrimaryAction);

        title.OpenInLibraryCommand.Execute(null);
        Assert.Equal((AppRoute.Item, (object?)"movie:the-matrix-1999"), Assert.Single(_harness.Navigation.Navigations));
    }

    [Fact]
    public async Task TheWatchlist_SaysWhetherAddingAlsoRequested()
    {
        _harness.Silo.On(HttpMethod.Put, "api/v2/watchlist/titles/movie/603", ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "media_type": "movie", "tmdb_id": 603, "added_at": "2026-10-01T12:00:00Z", "request": { "requestable": false, "status": "pending", "following": false, "requested_by_viewer": true } }"""));
        var title = await ShownAsync();
        Assert.True(title.ShowsWatchlist);
        Assert.Equal("Add to Watchlist", title.WatchlistLabel);

        await title.ToggleWatchlistCommand.ExecuteAsync(null);

        Assert.Equal("Added to your watchlist and requested. We'll let you know when The Matrix is available.", title.Notice);
        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Put, "api/v2/watchlist/titles/movie/603")[0].Body ?? "");
    }

    [Fact]
    public async Task MoreLikeThis_OpensTheLibrarysTitle_OrItsOwnPage()
    {
        var title = await ShownAsync();

        title.OpenCardCommand.Execute(title.Recommendations[0]);
        title.OpenCardCommand.Execute(title.Recommendations[1]);

        Assert.Equal(
        [
            (AppRoute.Title, (object?)new TitleRouteParameter("movie", 604, "The Matrix Reloaded")),
            (AppRoute.Item, "movie:the-matrix-revolutions-2003"),
        ], _harness.Navigation.Navigations.Select(n => (n.Route, n.Parameter)).ToList<(AppRoute, object?)>());
    }

    [Fact]
    public async Task ATitleTmdbDoesNotKnow_SaysItIsNotAvailable()
    {
        _harness.Answer(MoviePath, ScriptedSilo.Problem(404, "not_found"));

        var title = await ShownAsync();

        Assert.True(title.IsNotFound);
    }

    [Fact]
    public async Task ASeries_ShowsItsSeasons_AndAsksWhichToRequest()
    {
        var title = await ShownAsync("series", 1399);

        Assert.Equal("SERIES · HBO", title.Overline);
        Assert.Equal(["2019–2026", "4 SEASONS", "30 EPISODES", "RETURNING SERIES"], title.Chips.Select(c => c.Text));
        Assert.Equal("Created by A. Writer, B. Writer", title.CrewLine);
        Assert.Equal("Request series", title.PrimaryLabel);
        Assert.True(title.NeedsSeasonPicker);
        Assert.Equal("On Watchlist", title.WatchlistLabel);
        Assert.Equal(["Season 0: Specials", "Season 1", "Season 2: The Return", "Season 3", "Season 4"], title.Seasons.Select(s => s.Name));
        Assert.Equal(["", "In library", "", "Requested", "Not aired yet"], title.Seasons.Select(s => s.Status));
        Assert.Equal("2022 · 9 episodes", title.Seasons[2].Meta);
    }

    [Fact]
    public async Task RequestSeasons_StartsWithTheAiredMissingOnes_AndLeavesThemToTheServer()
    {
        var title = await ShownAsync("series", 1399);
        var picker = title.SeasonPicker()!;

        Assert.Equal("Choose the seasons of Example Show to request.", picker.Description);
        Assert.Equal([true, false, true, false, true], picker.Seasons.Select(s => s.IsChoosable));
        Assert.Equal([0, 2], picker.Seasons.Where(s => s.IsPicked && s.IsChoosable).Select(s => s.SeasonNumber));
        Assert.True(picker.Seasons.Single(s => s.SeasonNumber == 3).IsPicked);
        Assert.Equal("Request Seasons 0, 2", picker.SubmitLabel);
        Assert.Null(picker.PickedSeasons);

        await title.RequestSeasonsAsync(picker);
        var body = JsonDocument.Parse(Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, "api/v2/requests")).Body!).RootElement;
        Assert.False(body.TryGetProperty("seasons", out _));
    }

    [Fact]
    public async Task PickedSeasons_AreSent()
    {
        var title = await ShownAsync("series", 1399);
        var picker = title.SeasonPicker()!;

        picker.PickUpcomingCommand.Execute(null);
        Assert.Equal([4], picker.PickedSeasons!);
        Assert.Equal("Request Season 4", picker.SubmitLabel);
        Assert.False(picker.AllPicked);

        picker.AllPicked = true;
        Assert.Equal([0, 2, 4], picker.PickedSeasons!);

        picker.Seasons.Single(s => s.SeasonNumber == 0).IsPicked = false;
        Assert.False(picker.AllPicked);
        await title.RequestSeasonsAsync(picker);
        var body = JsonDocument.Parse(Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, "api/v2/requests")).Body!).RootElement;
        Assert.Equal([2, 4], body.GetProperty("seasons").EnumerateArray().Select(s => s.GetInt32()));
    }

    [Fact]
    public async Task NothingPicked_CannotBeSent()
    {
        var title = await ShownAsync("series", 1399);
        var picker = title.SeasonPicker()!;

        picker.AllPicked = true;
        picker.AllPicked = false;

        Assert.False(picker.CanSubmit);
        Assert.Equal("Request", picker.SubmitLabel);
    }

    [Fact]
    public async Task ASecondClick_WhileTheTitleIsReadAgain_SendsNothing()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(MoviePath, ScriptedSilo.Json(HttpStatusCode.OK, Movie), LibraryHarness.Held(release, MovieWith(PendingOwn)));
        _harness.Answer("api/v2/requests/mine", """{ "items": [], "page": { "has_more": false } }""");
        var title = await ShownAsync();

        var first = title.RequestCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _harness.Silo.SentTo(HttpMethod.Post, "api/v2/requests").Count == 1);

        Assert.False(title.CanAct);
        Assert.False(title.RequestCommand.CanExecute(null));
        // The title still reads as requestable until the answer comes; a click now changes nothing.
        await title.RequestCommand.ExecuteAsync(null);
        release.SetResult();
        await first;

        Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, "api/v2/requests"));
        Assert.Equal("Requested", title.PrimaryLabel);
        Assert.False(title.CanAct);
    }

    [Fact]
    public async Task ARequestThatWentThrough_ButCouldNotBeReadAfter_KeepsTheButtonsOffUntilReload()
    {
        _harness.Answer(MoviePath, ScriptedSilo.Json(HttpStatusCode.OK, Movie), ScriptedSilo.NetworkError(),
            ScriptedSilo.Json(HttpStatusCode.OK, MovieWith(PendingOwn)));
        _harness.Answer("api/v2/requests/mine", """{ "items": [], "page": { "has_more": false } }""");
        var title = await ShownAsync();

        await title.RequestCommand.ExecuteAsync(null);

        Assert.True(title.IsStale);
        Assert.False(title.CanAct);
        Assert.False(title.RequestCommand.CanExecute(null));
        Assert.Equal("Requested The Matrix. Reload to see where it stands now.", title.Notice);

        await title.RetryCommand.ExecuteAsync(null);
        Assert.False(title.IsStale);
        Assert.Equal("Requested", title.PrimaryLabel);
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, "api/v2/requests"));
    }

    [Fact]
    public async Task WithoutTheRequestsId_TheAccountsOpenRequestsAreSearched()
    {
        _harness.Answer(MoviePath, MovieWith("""
            "request": { "requestable": false, "reason": "already_requested", "status": "pending", "state": "pending", "following": false, "requested_by_viewer": true }
            """));
        _harness.Answer("api/v2/requests/mine",
            """{ "items": [ { "id": "x1", "media_type": "movie", "tmdb_id": 1, "title": "Other", "status": "pending", "outcome": "active", "state": "pending", "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-10-01T12:00:00Z", "updated_at": "2026-10-01T12:00:00Z", "is_anime": false, "provider": "silo", "source": "web" } ], "page": { "has_more": true, "next_cursor": "c1" } }""",
            """{ "items": [ { "id": "r9", "media_type": "movie", "tmdb_id": 603, "title": "The Matrix", "status": "pending", "outcome": "active", "state": "pending", "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-10-01T12:00:00Z", "updated_at": "2026-10-01T12:00:00Z", "is_anime": false, "provider": "silo", "source": "web" } ], "page": { "has_more": false } }""");

        var title = await ShownAsync();

        Assert.True(title.CanCancel);
        Assert.Equal(["outcome=active&limit=50", "outcome=active&limit=50&cursor=c1"], _harness.Queries("api/v2/requests/mine"));
    }

    [Fact]
    public void ATitleNeitherPageCanShow_OpensNothing()
    {
        var person = new RequestMediaResult { TmdbId = 7, MediaType = "person", Title = "Someone" };
        var noId = new RequestMediaResult { TmdbId = 0, MediaType = "movie", Title = "Nothing" };

        Assert.Null(RequestCards.Destination(person));
        Assert.Null(RequestCards.Destination(noId));
    }

    [Fact]
    public async Task SeasonsPickedBackToTheDefaults_AreSentAsPicked()
    {
        var title = await ShownAsync("series", 1399);
        var picker = title.SeasonPicker()!;
        var specials = picker.Seasons.Single(s => s.SeasonNumber == 0);

        specials.IsPicked = false;
        specials.IsPicked = true;

        Assert.Equal([0, 2], picker.PickedSeasons!);
    }

    [Fact]
    public void ASeriesInTheLibraryWithOnlyUpcomingSeasons_NeedsThemPicked()
    {
        var detail = new RequestMediaDetail
        {
            MediaType = "series",
            TmdbId = 9,
            Title = "Show",
            Availability = "available",
            Request = new RequestMediaState { Requestable = true },
            Seasons =
            [
                new RequestMediaSeason { SeasonNumber = 1, AirDate = "2020-01-01", EpisodeCount = 8, Availability = "available" },
                new RequestMediaSeason { SeasonNumber = 2, AirDate = "2027-01-01", EpisodeCount = 8, Availability = "missing" },
            ],
        };
        var picker = new RequestSeasonsViewModel(detail, new DateOnly(2026, 10, 1));

        Assert.False(picker.CanSubmit);
        Assert.Equal("Pick the upcoming seasons to request.", picker.Hint);

        picker.Seasons[1].IsPicked = true;
        Assert.True(picker.CanSubmit);
        Assert.Equal([2], picker.PickedSeasons!);
        Assert.Equal("", picker.Hint);
    }

    [Fact]
    public void TheLatestSeason_IsTheNewestAired_OrTheFirstAnnounced()
    {
        var today = new DateOnly(2026, 10, 1);
        RequestMediaSeason Season(int number, string? airDate, string availability = "missing", int episodes = 8) =>
            new() { SeasonNumber = number, AirDate = airDate, EpisodeCount = episodes, Availability = availability };

        Assert.Equal(2, TitleText.LatestSeason([Season(1, "2020-01-01"), Season(2, "2022-01-01"), Season(3, "2027-01-01")], today));
        Assert.Equal(1, TitleText.LatestSeason([Season(1, "2027-01-01"), Season(2, "2028-01-01")], today));
        Assert.Null(TitleText.LatestSeason([Season(1, "2020-01-01", "available")], today));
        Assert.Equal("Not announced", TitleText.SeasonMeta(Season(5, null, episodes: 0)));
        Assert.Equal("Not announced", TitleText.SeasonStatus(Season(5, null, episodes: 0), today));
        Assert.Equal("Partly in library", TitleText.SeasonStatus(Season(1, "2020-01-01", "partial"), today));
    }

    [Theory]
    [InlineData(999, "999 votes")]
    [InlineData(25840, "25.8K votes")]
    [InlineData(1250000, "1.3M votes")]
    public void Votes_AreCounted_Compactly(int count, string text) => Assert.Equal(text, TitleText.Votes(count));

    [Fact]
    public async Task TheApi_TakesOnlyMoviesAndSeries()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Api().GetDetailAsync(ScriptedSilo.Base, "../admin", 1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Api().GetDetailAsync(ScriptedSilo.Base, "movie", 0, TestContext.Current.CancellationToken));
    }
}
