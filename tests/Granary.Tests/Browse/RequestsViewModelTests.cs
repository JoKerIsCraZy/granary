using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Requests;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>The Requests hub, as Silo's web shows it; the clock stands on 1 October 2026, 12:00 UTC.</summary>
public sealed class RequestsViewModelTests
{
    private const string MinePath = "api/v2/requests/mine";
    private const string DiscoverPath = "api/v2/requests/discover";
    private const string StatusPath = "api/v2/requests/status";
    private const string StudiosPath = "api/v2/requests/discover/studios";
    private const string NetworksPath = "api/v2/requests/discover/networks";
    private const string GenresPath = "api/v2/requests/discover/genres";

    /// <summary>A studio with its logo, one without, one twice, one without a slug, and one whose logo is off TMDB.</summary>
    private const string Studios = """
        {
          "items": [
            { "tmdb_id": 420, "slug": "marvel-studios", "display_name": "Marvel Studios",
              "logo_url": "https://image.tmdb.org/t/p/w780_filter(duotone,ffffff,bababa)/marvel.png" },
            { "tmdb_id": 1, "slug": "a24", "display_name": "A24" },
            { "tmdb_id": 420, "slug": "marvel-studios", "display_name": "Marvel Again" },
            { "tmdb_id": 2, "slug": "", "display_name": "Nowhere" },
            { "tmdb_id": 3, "slug": "elsewhere", "display_name": "Elsewhere", "logo_url": "https://evil.example/logo.png" }
          ],
          "page": { "has_more": false }
        }
        """;

    /// <summary>A genre with its colours, and one with a colour that is not one.</summary>
    private const string Genres = """
        {
          "items": [
            { "slug": "action", "display_name": "Action", "gradient_from": "#7f1d1d", "gradient_to": "#1f2937", "series_supported": true },
            { "slug": "drama", "display_name": "Drama", "gradient_from": "red; background: url(x)" }
          ]
        }
        """;

    private const string NoBrands = """{ "items": [] }""";

    /// <summary>
    /// A pending request (cancellable), a series partly in (two seasons, downloading), one declined with a reason, one
    /// failed, one cancelled, and one available in the library — newest first.
    /// </summary>
    private const string Mine = """
        {
          "items": [
            { "id": "r1", "media_type": "movie", "tmdb_id": 1, "title": "Heat 2", "year": 2027, "poster_path": "/heat2.jpg",
              "status": "pending", "outcome": "active", "state": "pending", "seasons": [], "season_progress": [], "targets": [],
              "created_at": "2026-09-30T12:00:00Z", "updated_at": "2026-09-30T12:00:00Z", "is_anime": false, "provider": "silo", "source": "web" },
            { "id": "r2", "media_type": "series", "tmdb_id": 2, "title": "Severance", "year": 2022,
              "status": "downloading", "outcome": "active", "state": "partially_available", "seasons": [1, 2, 3, 5],
              "season_progress": [ { "season_number": 1, "episodes_aired": 9, "episodes_available": 9 }, { "season_number": 2, "episodes_aired": 10, "episodes_available": 4 } ],
              "download": { "phase": "downloading", "downloads": 1, "percent": 43.4, "estimated_completion_at": "2026-10-01T12:12:00Z", "updated_at": "2026-10-01T11:59:00Z" },
              "targets": [ { "id": "1" } ], "created_at": "2026-09-01T12:00:00Z", "updated_at": "2026-10-01T09:00:00Z", "is_anime": false, "provider": "silo", "source": "web" },
            { "id": "r3", "media_type": "movie", "tmdb_id": 3, "title": "Old Film", "status": "pending", "outcome": "declined", "state": "declined",
              "outcome_reason": "Not on any service", "seasons": [], "season_progress": [], "targets": [],
              "created_at": "2026-08-01T12:00:00Z", "updated_at": "2026-08-20T12:00:00Z", "is_anime": false, "provider": "silo", "source": "web" },
            { "id": "r4", "media_type": "movie", "tmdb_id": 4, "title": "Broken", "status": "approved", "outcome": "failed", "state": "failed",
              "last_error": "Radarr refused the title", "seasons": [], "season_progress": [], "targets": [ { "id": "2" } ],
              "created_at": "2026-07-01T12:00:00Z", "updated_at": "2026-07-01T12:00:30Z", "is_anime": false, "provider": "silo", "source": "web" },
            { "id": "r5", "media_type": "movie", "tmdb_id": 5, "title": "Changed Mind", "status": "pending", "outcome": "cancelled", "state": "cancelled",
              "seasons": [], "season_progress": [], "targets": [],
              "created_at": "2026-06-01T12:00:00Z", "updated_at": "2026-06-01T12:00:00Z", "is_anime": false, "provider": "silo", "source": "web" },
            { "id": "r6", "media_type": "movie", "tmdb_id": 6, "title": "Dune", "year": 2021, "status": "completed", "outcome": "active", "state": "available",
              "library_content_id": "movie:dune-2021", "seasons": [], "season_progress": [], "targets": [ { "id": "3" } ],
              "created_at": "2026-05-01T12:00:00Z", "updated_at": "2026-05-03T12:00:00Z", "is_anime": false, "provider": "silo", "source": "web" }
          ],
          "page": { "has_more": false }
        }
        """;

    private const string NoRequests = """{ "items": [], "page": { "has_more": false } }""";

    /// <summary>A row with a requestable title, one already requested by someone, one in the library, and one blocked.</summary>
    private const string Discover = """
        {
          "items": [
            {
              "key": "trending_movies", "title": "Trending Movies", "page": 1, "total_pages": 5, "total_results": 100,
              "results": [
                { "tmdb_id": 10, "media_type": "movie", "title": "New One", "year": 2026, "poster_path": "/new.jpg", "availability": "none", "in_watchlist": false,
                  "request": { "requestable": true, "following": false, "requested_by_viewer": false } },
                { "tmdb_id": 11, "media_type": "series", "title": "Asked For", "year": 2025, "availability": "none", "in_watchlist": false,
                  "request": { "requestable": false, "reason": "already_requested", "status": "approved", "state": "approved", "following": true, "requested_by_viewer": false } },
                { "tmdb_id": 12, "media_type": "movie", "title": "Got It", "year": 2020, "availability": "available", "library_content_id": "movie:got-it-2020",
                  "in_watchlist": false, "request": { "requestable": false, "reason": "already_available", "following": false, "requested_by_viewer": false } },
                { "tmdb_id": 13, "media_type": "movie", "title": "Not For You", "availability": "none", "in_watchlist": false,
                  "request": { "requestable": false, "reason": "blocked", "following": false, "requested_by_viewer": false } }
              ]
            },
            { "key": "upcoming_movies", "title": "Upcoming Movies", "page": 1, "total_pages": 1, "total_results": 0, "results": [] }
          ]
        }
        """;

    private readonly LibraryHarness _harness = new();

    public RequestsViewModelTests()
    {
        _harness.Answer(MinePath, Mine);
        _harness.Answer(DiscoverPath, Discover);
        _harness.Answer(StudiosPath, Studios);
        _harness.Answer(NetworksPath, NoBrands);
        _harness.Answer(GenresPath, Genres);
        _harness.Silo.On(HttpMethod.Post, "api/v2/requests/r1/cancel", ScriptedSilo.Json(HttpStatusCode.OK, """
            { "id": "r1", "media_type": "movie", "tmdb_id": 1, "title": "Heat 2", "status": "pending", "outcome": "cancelled", "state": "cancelled",
              "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-09-30T12:00:00Z", "updated_at": "2026-10-01T12:00:00Z",
              "is_anime": false, "provider": "silo", "source": "web" }
            """));
    }

    private RequestsViewModel Create() =>
        new(_harness.Session, new RequestsApi(_harness.Silo.Http), _harness.Navigation, _harness.Clock, new ImmediateDispatcher(),
            _harness.Logs.For<RequestsViewModel>());

    private RequestsFeature Feature() => new(_harness.Session, new RequestsApi(_harness.Silo.Http), _harness.Clock, _harness.Logs.For<RequestsFeature>());

    /// <summary><see cref="Mine"/> after the pending request was withdrawn.</summary>
    private static string MineAfterCancel => Mine.Replace(
        "\"status\": \"pending\", \"outcome\": \"active\", \"state\": \"pending\"",
        "\"status\": \"pending\", \"outcome\": \"cancelled\", \"state\": \"cancelled\"", StringComparison.Ordinal);

    private async Task<RequestsViewModel> LoadedAsync()
    {
        var hub = Create();
        await hub.ActivateAsync(isReturn: false);
        return hub;
    }

    private static RequestRowViewModel Row(RequestsViewModel hub, string id) => hub.RequestGroups.SelectMany(g => g.Rows).Single(r => r.Id == id);

    [Fact]
    public async Task Yours_GroupsTheRequestsByWhatHappensNext_InTheServersOrder()
    {
        var hub = await LoadedAsync();

        Assert.Equal(["Needs attention", "On the way", "In your library", "Cancelled"], hub.RequestGroups.Select(g => g.Title));
        Assert.Equal([["r3", "r4"], ["r1", "r2"], ["r6"], ["r5"]], hub.RequestGroups.Select(g => g.Rows.Select(r => r.Id).ToArray()));
        Assert.Equal("6", hub.YoursCount);
        Assert.Equal("limit=50", _harness.Queries(MinePath)[0]);
    }

    [Fact]
    public async Task ARow_SaysWhereTheRequestStands()
    {
        var hub = await LoadedAsync();

        var series = Row(hub, "r2");
        Assert.Equal("Partially available", series.StateLabel);
        Assert.Equal(RequestBadgeLook.Secondary, series.StateLook);
        Assert.Equal("Series · 2022 · Seasons 1–3, 5 · 13 of 19 episodes in the library", series.Details);
        Assert.Equal("Downloading · 43.4% · about 12 min left", series.DownloadText);
        Assert.Equal(43.4, series.DownloadPercent);
        Assert.Equal("Requested Sep 1, 2026 · Updated 3h ago", series.Dates);
        Assert.False(series.CanCancel);

        var pending = Row(hub, "r1");
        Assert.Equal(RequestBadgeLook.Outline, pending.StateLook);
        Assert.Equal("Movie · 2027", pending.Details);
        Assert.Equal("Requested Sep 30, 2026", pending.Dates);
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w154/heat2.jpg"), pending.PosterUrl);
        Assert.True(pending.CanCancel);

        Assert.Equal("Reason: Not on any service", Row(hub, "r3").Note);
        Assert.False(Row(hub, "r3").IsError);
        Assert.Equal("Radarr refused the title", Row(hub, "r4").Note);
        Assert.True(Row(hub, "r4").IsError);
        Assert.Equal(RequestBadgeLook.Destructive, Row(hub, "r4").StateLook);
        Assert.Equal(RequestBadgeLook.Primary, Row(hub, "r6").StateLook);
        Assert.Equal(RequestBadgeLook.Muted, Row(hub, "r5").StateLook);
    }

    [Fact]
    public async Task ARequestThatArrived_OpensInTheLibrary()
    {
        var hub = await LoadedAsync();

        hub.OpenInLibraryCommand.Execute(Row(hub, "r6"));

        Assert.Equal((AppRoute.Item, (object?)"movie:dune-2021"), Assert.Single(_harness.Navigation.Navigations));
        Assert.False(Row(hub, "r1").CanOpenInLibrary);
    }

    [Fact]
    public async Task Cancelling_WithdrawsTheRequest_AndReadsTheListAgain()
    {
        var hub = await LoadedAsync();

        await hub.CancelCommand.ExecuteAsync(Row(hub, "r1"));

        Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, "api/v2/requests/r1/cancel"));
        Assert.Equal(2, _harness.Count(MinePath));
        Assert.False(hub.HasNotice);
    }

    [Fact]
    public async Task ACancellationThatFails_SaysSo_AndCanBeTriedAgain()
    {
        _harness.Silo.On(HttpMethod.Post, "api/v2/requests/r1/cancel", ScriptedSilo.Problem(409, "conflict"));
        var hub = await LoadedAsync();
        var row = Row(hub, "r1");

        await hub.CancelCommand.ExecuteAsync(row);

        Assert.Equal(RequestsViewModel.CancelFailedText, hub.Notice);
        Assert.False(row.IsCancelling);
        Assert.True(Row(hub, "r1").CanCancel);
    }

    [Fact]
    public async Task ARequestThatCannotBeWithdrawn_IsNotSent()
    {
        var hub = await LoadedAsync();

        await hub.CancelCommand.ExecuteAsync(Row(hub, "r2"));

        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Post, "api/v2/requests/r2/cancel"));
    }

    [Fact]
    public async Task NoRequests_SaysSo()
    {
        _harness.Answer(MinePath, NoRequests);

        var hub = await LoadedAsync();

        Assert.True(hub.IsYoursEmpty);
        Assert.False(hub.HasYoursCount);
    }

    [Fact]
    public async Task ManyRequests_AreReadUpToAHundred()
    {
        string Page(int from, bool more) => $$"""
            { "items": [ {{string.Join(",", Enumerable.Range(from, 50).Select(i => $$"""{ "id": "q{{i}}", "media_type": "movie", "tmdb_id": {{i}}, "title": "T{{i}}", "status": "pending", "outcome": "active", "state": "pending", "seasons": [], "season_progress": [], "targets": [], "created_at": "2026-09-01T12:00:00Z", "updated_at": "2026-09-01T12:00:00Z", "is_anime": false, "provider": "silo", "source": "web" }"""))}} ],
              "page": { "has_more": {{(more ? "true" : "false")}}, "next_cursor": "{{(more ? "c" + from : "")}}" } }
            """;
        _harness.Answer(MinePath, Page(0, true), Page(50, true));

        var hub = await LoadedAsync();

        Assert.Equal(["limit=50", "limit=50&cursor=c0"], _harness.Queries(MinePath));
        Assert.Equal("100", hub.YoursCount);
    }

    [Fact]
    public async Task Discover_ShowsEachRowsTitles_WithWhereTheyStand()
    {
        var hub = await LoadedAsync();

        var row = Assert.Single(hub.DiscoverRows);
        Assert.Equal("Trending Movies", row.Title);
        Assert.Equal(["New One", "Asked For", "Got It", "Not For You"], row.Cards.Select(c => c.Title));
        Assert.Equal([null, "APPROVED", "AVAILABLE", "BLOCKED"], row.Cards.Select(c => c.StatusBadge));
        Assert.Equal(["2026", "2025 SERIES", "2020", ""], row.Cards.Select(c => c.Subtitle));
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w342/new.jpg"), row.Cards[0].ImageUrl);
        Assert.All(row.Cards, c => Assert.Equal(RequestsViewModel.CardType, c.Type));
    }

    [Fact]
    public async Task ADiscoverTitle_OpensInTheLibrary_OrOnItsOwnPage()
    {
        var hub = await LoadedAsync();
        var cards = hub.DiscoverRows[0].Cards;

        hub.OpenCardCommand.Execute(cards[2]);
        hub.OpenCardCommand.Execute(cards[1]);

        Assert.Equal(
        [
            (AppRoute.Item, (object?)"movie:got-it-2020"),
            (AppRoute.Title, new TitleRouteParameter("series", 11, "Asked For")),
        ], _harness.Navigation.Navigations.Select(n => (n.Route, n.Parameter)).ToList<(AppRoute, object?)>());
    }

    [Fact]
    public async Task ARowWithMorePages_OpensInFull()
    {
        var hub = await LoadedAsync();

        var exploreAll = hub.DiscoverRows[0].ExploreAllCommand;
        Assert.NotNull(exploreAll);
        exploreAll.Execute(null);

        var navigation = Assert.Single(_harness.Navigation.Navigations);
        Assert.Equal(AppRoute.RequestTitles, navigation.Route);
        Assert.Equal(new RequestTitlesRouteParameter("trending_movies", "Trending Movies"), navigation.Parameter);
    }

    [Fact]
    public async Task ARowOfOnePage_HasNothingMoreToShow()
    {
        _harness.Answer(DiscoverPath, Discover.Replace("\"total_pages\": 5", "\"total_pages\": 1", StringComparison.Ordinal));

        var hub = await LoadedAsync();

        Assert.Null(hub.DiscoverRows[0].ExploreAllCommand);
    }

    [Fact]
    public async Task Studios_NetworksAndGenres_FollowTheRows()
    {
        var hub = await LoadedAsync();

        Assert.Equal(["Studios", "Networks", "Genres"], hub.BrandRows.Select(r => r.Title));
        var studios = hub.BrandRows[0];
        Assert.True(studios.IsReady);
        // A brand shows once, and only one with somewhere to go.
        Assert.Equal(["Marvel Studios", "A24", "Elsewhere"], studios.Tiles.Select(t => t.Name));
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w780_filter(duotone,ffffff,bababa)/marvel.png"), studios.Tiles[0].Logo);
        Assert.Equal([true, false, false], studios.Tiles.Select(t => t.HasLogo));
        // A row without any stays hidden.
        Assert.False(hub.BrandRows[1].IsShown);

        var genres = hub.BrandRows[2].Tiles;
        Assert.All(genres, t => Assert.True(t.IsGenre));
        Assert.Equal(("#7f1d1d", "#1f2937"), (genres[0].GradientFrom, genres[0].GradientTo));
        // The web's colours stand in for ones that are not colours.
        Assert.Equal(("#475569", "#0f172a"), (genres[1].GradientFrom, genres[1].GradientTo));
    }

    [Fact]
    public async Task ATile_OpensItsTitles_AGenreOnItsMovies()
    {
        var hub = await LoadedAsync();
        var studio = hub.BrandRows[0].Tiles[0];
        var genre = hub.BrandRows[2].Tiles[0];

        studio.OpenCommand.Execute(studio);
        genre.OpenCommand.Execute(genre);

        Assert.Equal(
        [
            new RequestTitlesRouteParameter("marvel-studios", "Marvel Studios", DiscoverBrowseKind.Studio),
            new RequestTitlesRouteParameter("action", "Action", DiscoverBrowseKind.Genre, "movie"),
        ], _harness.Navigation.Navigations.Where(n => n.Route == AppRoute.RequestTitles).Select(n => n.Parameter));
    }

    [Fact]
    public async Task ABrandRowThatFails_SaysSo_AndCanBeTriedAgainOnItsOwn()
    {
        _harness.Answer(NetworksPath, ScriptedSilo.Problem(502, "bad_gateway"),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "items": [ { "slug": "hbo", "display_name": "HBO" } ] }"""));
        var hub = await LoadedAsync();
        var networks = hub.BrandRows[1];
        Assert.True(networks.HasError);
        Assert.True(networks.IsShown);
        Assert.Equal("Could not load networks.", networks.FailureText);

        await networks.RetryCommand.ExecuteAsync(null);

        Assert.Equal(["HBO"], networks.Tiles.Select(t => t.Name));
        Assert.Equal(1, _harness.Count(StudiosPath));
        Assert.Equal(1, _harness.Count(DiscoverPath));
    }

    [Fact]
    public async Task ATabThatFails_CanBeTriedAgain_WithoutTheOther()
    {
        _harness.Answer(DiscoverPath, ScriptedSilo.Problem(502, "bad_gateway"), ScriptedSilo.Json(HttpStatusCode.OK, Discover));

        var hub = await LoadedAsync();
        Assert.True(hub.HasDiscoverError);
        Assert.Equal(4, hub.RequestGroups.Count);

        await hub.RetryDiscoverCommand.ExecuteAsync(null);
        Assert.Single(hub.DiscoverRows);
        Assert.Equal(1, _harness.Count(MinePath));
    }

    [Fact]
    public async Task Back_KeepsTheHub()
    {
        var hub = await LoadedAsync();
        hub.SelectTabCommand.Execute(RequestsTab.Yours);

        await hub.ActivateAsync(isReturn: true);

        Assert.Equal(1, _harness.Count(MinePath));
        Assert.True(hub.IsYoursTab);
    }

    [Fact]
    public async Task TheSidebar_ShowsRequestsOnlyWhenTheServerTakesThem()
    {
        _harness.Answer(StatusPath, """{ "requests_enabled": true, "follow_supported": true, "state": "available", "allowed": true }""");
        var feature = Feature();

        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(feature.IsEnabled);
        Assert.Equal(1, _harness.Count(StatusPath));

        _harness.Session.ProfileId = "8";
        _harness.Answer(StatusPath, ScriptedSilo.NetworkError(), ScriptedSilo.Json(HttpStatusCode.OK, """{ "requests_enabled": false }"""));
        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(feature.IsEnabled);
        // A failure is not asked again on every page, only after a while.
        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _harness.Count(StatusPath));
        _harness.Clock.Advance(RequestsFeature.RetryAfter);
        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(feature.IsEnabled);
        Assert.Equal(3, _harness.Count(StatusPath));
    }

    [Fact]
    public async Task AServerWithoutRequests_IsNotAskedAgain()
    {
        _harness.Answer(StatusPath, ScriptedSilo.Problem(404, "not_found"));
        var feature = Feature();

        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        _harness.Clock.Advance(RequestsFeature.RetryAfter);
        await feature.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(feature.IsEnabled);
        Assert.Equal(1, _harness.Count(StatusPath));
    }

    [Fact]
    public async Task TwoPagesAskingAtOnce_SendOneRead_AndBothHearItsAnswer()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(StatusPath, LibraryHarness.Held(release, """{ "requests_enabled": true }"""));
        var feature = Feature();

        var first = feature.RefreshAsync(TestContext.Current.CancellationToken);
        var second = feature.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        release.SetResult();
        await second;

        Assert.True(feature.IsEnabled);
        await first;
        Assert.Equal(1, _harness.Count(StatusPath));
    }

    [Fact]
    public async Task AFirstCallerGivingUp_LeavesTheReadToTheOthers()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(StatusPath, LibraryHarness.Held(release, """{ "requests_enabled": true }"""));
        var feature = Feature();
        using var giveUp = new CancellationTokenSource();

        var first = feature.RefreshAsync(giveUp.Token);
        var second = feature.RefreshAsync(TestContext.Current.CancellationToken);
        giveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        await second;

        Assert.True(feature.IsEnabled);
        Assert.Equal(1, _harness.Count(StatusPath));
    }

    [Fact]
    public async Task ACancelRacingAReload_EndsWithTheListAfterTheCancel()
    {
        var hub = await LoadedAsync();
        var row = Row(hub, "r1");
        var release = new TaskCompletionSource();
        _harness.Answer(MinePath, LibraryHarness.Held(release, Mine), ScriptedSilo.Json(HttpStatusCode.OK, MineAfterCancel));
        var reload = hub.ActivateAsync(isReturn: false);

        await hub.CancelCommand.ExecuteAsync(row);
        release.SetResult();
        await reload;

        Assert.Equal(LibraryLoadState.Ready, hub.YoursState);
        Assert.Contains("r1", hub.RequestGroups.Single(g => g.Title == "Cancelled").Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task ARefusedCancel_ReadsTheListAgain()
    {
        _harness.Silo.On(HttpMethod.Post, "api/v2/requests/r1/cancel", ScriptedSilo.Problem(409, "conflict"));
        var hub = await LoadedAsync();

        await hub.CancelCommand.ExecuteAsync(Row(hub, "r1"));

        Assert.Equal(2, _harness.Count(MinePath));
        Assert.Equal(RequestsViewModel.CancelFailedText, hub.Notice);
        Assert.Equal(LibraryLoadState.Ready, hub.YoursState);
    }

    [Fact]
    public async Task AQuietReadThatFails_KeepsTheRowsShown()
    {
        var hub = await LoadedAsync();
        _harness.Clock.Advance(RequestsViewModel.DownloadPollInterval);
        await WaitUntilAsync(() => _harness.Count(MinePath) == 2);
        _harness.Answer(MinePath, ScriptedSilo.NetworkError());
        _harness.Clock.Advance(RequestsViewModel.DownloadPollInterval);

        await hub.ActivateAsync(isReturn: true);

        Assert.Equal(LibraryLoadState.Ready, hub.YoursState);
        Assert.Equal(4, hub.RequestGroups.Count);
    }

    [Fact]
    public async Task ADownload_IsFollowedWhileThePageShows()
    {
        var hub = await LoadedAsync();

        _harness.Clock.Advance(RequestsViewModel.DownloadPollInterval);
        await WaitUntilAsync(() => _harness.Count(MinePath) == 2);

        hub.Deactivate();
        _harness.Clock.Advance(RequestsViewModel.DownloadPollInterval * 3);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, _harness.Count(MinePath));
    }

    [Fact]
    public async Task NothingDownloading_IsNotReadAgain()
    {
        _harness.Answer(MinePath, NoRequests);
        var hub = await LoadedAsync();

        _harness.Clock.Advance(RequestsViewModel.DownloadPollInterval * 2);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(1, _harness.Count(MinePath));
        Assert.True(hub.IsYoursEmpty);
    }

    [Fact]
    public async Task BackAfterAWhile_ReadsYoursAgainQuietly()
    {
        _harness.Answer(MinePath, NoRequests);
        var hub = await LoadedAsync();

        await hub.ActivateAsync(isReturn: true);
        Assert.Equal(1, _harness.Count(MinePath));

        _harness.Clock.Advance(RequestsViewModel.DownloadPollInterval);
        await hub.ActivateAsync(isReturn: true);
        Assert.Equal(2, _harness.Count(MinePath));
        Assert.Equal(1, _harness.Count(DiscoverPath));
    }

    [Fact]
    public async Task AnotherProfile_ShowsNothingOfTheLastOnes()
    {
        var hub = await LoadedAsync();
        _harness.Session.ProfileId = "8";
        _harness.Answer(MinePath, ScriptedSilo.Problem(503, "unavailable"));

        await hub.ActivateAsync(isReturn: true);

        Assert.Empty(hub.RequestGroups);
        Assert.False(hub.HasYoursCount);
        Assert.True(hub.HasYoursError);
    }

    [Fact]
    public void Changes_ReadAsTheWebs()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        MediaRequest Changed(TimeSpan ago, TimeSpan after) => new() { CreatedAt = now - ago - after, UpdatedAt = now - ago };

        Assert.Null(RequestText.LastChange(Changed(TimeSpan.FromHours(1), TimeSpan.FromSeconds(59)), now, TimeZoneInfo.Utc));
        Assert.Equal("just now", RequestText.LastChange(Changed(TimeSpan.FromSeconds(20), TimeSpan.FromDays(1)), now, TimeZoneInfo.Utc));
        Assert.Equal("59m ago", RequestText.LastChange(Changed(TimeSpan.FromMinutes(59), TimeSpan.FromDays(1)), now, TimeZoneInfo.Utc));
        Assert.Equal("1h ago", RequestText.LastChange(Changed(TimeSpan.FromMinutes(60), TimeSpan.FromDays(1)), now, TimeZoneInfo.Utc));
        Assert.Equal("6d ago", RequestText.LastChange(Changed(TimeSpan.FromDays(6), TimeSpan.FromDays(1)), now, TimeZoneInfo.Utc));
        Assert.Equal("Sep 24, 2026", RequestText.LastChange(Changed(TimeSpan.FromDays(7), TimeSpan.FromDays(1)), now, TimeZoneInfo.Utc));
        // A date is the viewer's: 23:30 UTC is already the next day in Berlin.
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        Assert.Equal("Oct 2, 2026", RequestText.Date(new DateTimeOffset(2026, 10, 1, 23, 30, 0, TimeSpan.Zero), berlin));
    }

    [Fact]
    public void SeasonProgress_CountsWhatAiredAndArrived()
    {
        Assert.Equal("", RequestText.SeasonProgress([new RequestSeasonProgress(1, 0, 0)]));
        Assert.Equal("1 of 1 episode in the library", RequestText.SeasonProgress([new RequestSeasonProgress(1, 1, 3)]));
        Assert.Equal("2 of 5 episodes in the library", RequestText.SeasonProgress([new RequestSeasonProgress(1, 3, 2), new RequestSeasonProgress(2, 2, 0)]));
    }

    [Theory]
    [InlineData("pending", "active", 0, true)]
    [InlineData("approved", "active", 0, true)]
    [InlineData("approved", "active", 1, false)]
    [InlineData("pending", "declined", 0, false)]
    [InlineData("pending", "", 0, false)]
    [InlineData("queued", "active", 0, false)]
    public void ARequest_CanBeWithdrawnUntilSomethingWasSent(string status, string outcome, int targets, bool cancellable)
    {
        var target = System.Text.Json.JsonDocument.Parse("{}").RootElement;
        var request = new MediaRequest { Status = status, Outcome = outcome, Targets = [.. Enumerable.Repeat(target, targets)] };

        Assert.Equal(cancellable, RequestText.CanCancel(request));
    }

    [Fact]
    public void AStateThisAppDoesNotKnow_ReadsActive_AndMuted()
    {
        Assert.Equal("something_new", RequestText.DisplayState("pending", "active", "something_new"));
        Assert.Equal("Active", RequestText.StateLabel("something_new"));
        Assert.Equal(RequestBadgeLook.Muted, RequestText.Look("something_new"));
        Assert.Equal("pending", RequestText.DisplayState("pending", "active", null));
    }

    [Theory]
    [InlineData(new[] { 2 }, "Season 2")]
    [InlineData(new[] { 3, 1, 2, 5 }, "Seasons 1–3, 5")]
    [InlineData(new[] { 1, 1, 4, 6, 7 }, "Seasons 1, 4, 6–7")]
    public void Seasons_AreNamedCompactly(int[] seasons, string text) => Assert.Equal(text, RequestText.SeasonList(seasons));

    [Theory]
    [InlineData("queued", "Waiting to download")]
    [InlineData("import_blocked", "Waiting for import")]
    [InlineData("something_new", "Downloading")]
    public void ADownloadsPhase_IsWordedAsTheWebs(string phase, string text) =>
        Assert.Equal(text, RequestText.Download(new RequestDownload { Phase = phase, Percent = 10 }, DateTimeOffset.UnixEpoch));

    [Fact]
    public void AnOldEstimate_IsLeftOut()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var stale = new RequestDownload { Phase = "downloading", Percent = 50, EstimatedCompletionAt = now.AddHours(2), UpdatedAt = now.AddMinutes(-11) };
        var fresh = stale with { UpdatedAt = now.AddMinutes(-1), EstimatedCompletionAt = now.AddMinutes(150) };

        Assert.Equal("Downloading · 50%", RequestText.Download(stale, now));
        Assert.Equal("Downloading · 50% · about 2 hr 30 min left", RequestText.Download(fresh, now));
    }

    [Fact]
    public void AnOlderServer_StillGetsAState()
    {
        Assert.Equal("processing", RequestText.DisplayState("queued", "active", null));
        Assert.Equal("available", RequestText.DisplayState("completed", "active", ""));
        Assert.Equal("declined", RequestText.DisplayState("pending", "declined", null));
        Assert.Null(RequestText.DisplayState("unknown", "active", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-slash.jpg")]
    [InlineData("/../x.jpg")]
    [InlineData("/%2e%2e/x.jpg")]
    [InlineData("/x.jpg?y=1")]
    [InlineData("/x.jpg#y")]
    [InlineData("/x\n.jpg")]
    [InlineData("//evil.example/x.jpg")]
    [InlineData("/x.exe")]
    public void ATmdbPathThatIsNotOne_HasNoPoster(string? path) => Assert.Null(TmdbImages.Poster(path));

    [Fact]
    public void ATmdbPath_StaysOnTmdbsImageServer()
    {
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w154/abc_DEF-1.jpg"), TmdbImages.Poster("/abc_DEF-1.jpg", "w154"));
        Assert.Null(TmdbImages.Poster("/" + new string('a', 200) + ".jpg"));
        Assert.Null(TmdbImages.Poster("/abc.jpg", "../w154"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
}
