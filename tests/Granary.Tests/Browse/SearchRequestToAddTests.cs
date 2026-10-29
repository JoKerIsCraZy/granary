using Granary.Core.Abstractions;
using Granary.Core.Api.Requests;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

/// <summary>"Request to add" under Search's results, as the web's RequestToAddSection shows it on the catalog search.</summary>
public sealed class SearchRequestToAddTests
{
    internal const string StatusPath = "api/v2/requests/status";
    private const string CatalogPath = "api/v2/catalog";
    private const string RequestSearchPath = "api/v2/requests/search";

    private readonly BrowseFakeSilo _silo = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly RecordingNavigation _navigation = new();
    private readonly ManualTimeProvider _time = new();

    public SearchRequestToAddTests()
    {
        _silo.Map("api/v2/catalog/search/capabilities", SearchCapabilities(people: false));
        // The library has nothing, unless a test says otherwise.
        _silo.Map(CatalogPath, Page(null));
        _silo.Map(StatusPath, """{ "requests_enabled": true }""");
        _silo.Map(RequestSearchPath, _ => BrowseFakeSilo.Json(Results(1, 1, (10, "none"), (11, "available"), (12, "partial"))));
    }

    internal static SearchViewModel Create(BrowseFakeSilo silo, FakeSessionManager session, RecordingNavigation navigation, ManualTimeProvider time)
    {
        var requests = new RequestsApi(silo.Http);
        var feature = new RequestsFeature(session, requests, time, silo.Logs.For<RequestsFeature>());
        var requestToAdd = new RequestToAddViewModel(session, requests, feature, navigation, silo.Logs.For<RequestToAddViewModel>());
        return new SearchViewModel(session, silo.Catalog, silo.Images, navigation, new ImmediateDispatcher(), time, requestToAdd,
            silo.Logs.For<SearchViewModel>());
    }

    private SearchViewModel Create() => Create(_silo, _session, _navigation, _time);

    /// <summary>A page of the request search with movies of these TMDB ids ("Movie 10") and their availability.</summary>
    private static string Results(int page, int totalPages, params (int Id, string Availability)[] titles) =>
        $$"""
        { "page": {{page}}, "total_pages": {{totalPages}}, "total_results": {{totalPages * 20}},
          "results": [{{string.Join(",", titles.Select(t => $$"""
            { "tmdb_id": {{t.Id}}, "media_type": "movie", "title": "Movie {{t.Id}}", "year": 2020, "poster_path": "/m{{t.Id}}.jpg",
              "availability": "{{t.Availability}}", {{(t.Availability == "available" ? $"\"library_content_id\": \"movie:m{t.Id}\"," : "")}}
              "in_watchlist": false, "request": { "requestable": {{(t.Availability == "none" ? "true" : "false")}}, "following": false,
              "requested_by_viewer": false } }
            """))}}] }
        """;

    /// <summary>Every page of <paramref name="totalPages"/> holds <paramref name="perPage"/> new titles to add, ids page × 100 + 1, 2, ….</summary>
    private void Pages(int totalPages, int perPage) =>
        _silo.Map(RequestSearchPath, request =>
        {
            int page = Number(request, "page");
            return BrowseFakeSilo.Json(Results(page, totalPages, [.. Enumerable.Range(1, perPage).Select(i => (page * 100 + i, "none"))]));
        });

    private static int Number(HttpRequestMessage request, string name) => int.Parse(Read(request, name) ?? "0", System.Globalization.CultureInfo.InvariantCulture);

    private static string? Read(HttpRequestMessage request, string name) => new BrowseRequest(request.Method, request.RequestUri!, null).Query(name);

    private static string[] Titles(SearchViewModel search) => [.. search.Requests.Cards.Select(c => c.Title)];

    private string[] PagesRead() => [.. _silo.To(RequestSearchPath).Select(r => r.Query("page") ?? "")];

    [Fact]
    public async Task ATextTheLibraryLacks_ShowsWhatTmdbFinds_WithoutTheTitlesTheLibraryHas()
    {
        var search = Create();

        await search.ActivateAsync("zzz");

        var sent = Assert.Single(_silo.To(RequestSearchPath));
        Assert.Equal(("zzz", "all", "1"), (sent.Query("q"), sent.Query("media_type"), sent.Query("page")));
        Assert.Equal(["Movie 10", "Movie 12"], Titles(search));
        Assert.True(search.ShowsRequests);
        Assert.Equal("Nothing in your library matches “zzz”.", search.Requests.Note);
        // The section says it instead of "No titles found", and the empty library's "Titles" heading goes.
        Assert.True(search.IsLibraryRescued);
        Assert.Equal("", search.StatusText);
        Assert.False(search.ShowsTitlesHeading);
        Assert.False(search.Requests.CanLoadMore);
        Assert.False(search.Requests.ShowsLoadMore);
    }

    [Fact]
    public async Task ATextTheLibraryHas_ShowsTheSectionWithoutTheNote()
    {
        _silo.Map(CatalogPath, Page(null, Item("heat-1995")));
        var search = Create();

        await search.ActivateAsync("heat");

        Assert.Equal(["heat-1995"], search.Cards.Select(c => c.Id));
        Assert.True(search.ShowsRequests);
        Assert.Equal("", search.Requests.Note);
        Assert.False(search.IsLibraryRescued);
    }

    [Theory]
    [InlineData(SearchScope.Movies, "movie")]
    [InlineData(SearchScope.Series, "series")]
    public async Task TheTypeSwitch_SearchesTmdbForItsType(SearchScope scope, string mediaType)
    {
        var search = Create();
        await search.ActivateAsync("heat");

        search.Scope = scope;
        await search.Searching;

        Assert.Equal(["all", mediaType], _silo.To(RequestSearchPath).Select(r => r.Query("media_type")));
    }

    [Fact]
    public async Task AServerThatTakesNoRequests_IsNotAsked_AndTheLibrarySaysItFoundNothing()
    {
        _silo.Map(StatusPath, """{ "requests_enabled": false }""");
        var search = Create();

        await search.ActivateAsync("zzz");

        Assert.Empty(_silo.To(RequestSearchPath));
        Assert.False(search.ShowsRequests);
        Assert.Equal("No titles found for \"zzz\"", search.StatusText);
    }

    [Fact]
    public async Task OneCharacter_IsNotSearchedOnTmdb()
    {
        var search = Create();

        await search.ActivateAsync("z");

        Assert.Single(_silo.To(CatalogPath));
        Assert.Empty(_silo.To(RequestSearchPath));
        Assert.Equal("No titles found for \"z\"", search.StatusText);
    }

    // The user's "Borat": six titles to add on TMDB's first page, and "Page 1 of 2" under a grid with room to spare.
    [Fact]
    public async Task AFewTitlesOnAPage_GatherTheNextPages_WithoutAPager()
    {
        Pages(totalPages: 10, perPage: 6);
        var search = Create();

        await search.ActivateAsync("borat");

        Assert.Equal(["1", "2", "3"], PagesRead());
        Assert.Equal(18, search.Requests.Cards.Count);
        Assert.Equal(["Movie 101", "Movie 106", "Movie 201", "Movie 306"], new[] { 0, 5, 6, 17 }.Select(i => search.Requests.Cards[i].Title));
        Assert.True(search.Requests.CanLoadMore);
        Assert.True(search.Requests.ShowsLoadMore);
    }

    [Fact]
    public async Task AScreenful_EndsTheRead()
    {
        Pages(totalPages: 5, perPage: 20);
        var search = Create();

        await search.ActivateAsync("heat");

        Assert.Equal(["1"], PagesRead());
        Assert.Equal(20, search.Requests.Cards.Count);
        Assert.True(search.Requests.CanLoadMore);
    }

    [Fact]
    public async Task TheGridsEnd_ReadsOn_ToTmdbsLastPage()
    {
        Pages(totalPages: 4, perPage: 20);
        var search = Create();
        await search.ActivateAsync("heat");

        await search.Requests.LoadMoreCommand.ExecuteAsync(null);
        await search.Requests.LoadMoreCommand.ExecuteAsync(null);
        await search.Requests.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(["1", "2", "3", "4"], PagesRead());
        Assert.Equal(80, search.Requests.Cards.Count);
        Assert.False(search.Requests.CanLoadMore);
        Assert.False(search.Requests.LoadMoreCommand.CanExecute(null));
        await search.Requests.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(4, PagesRead().Length);
    }

    [Fact]
    public async Task ATitleOnTwoPages_ShowsOnce()
    {
        _silo.Map(RequestSearchPath, request => Number(request, "page") == 1
            ? BrowseFakeSilo.Json(Results(1, 2, (1, "none"), (2, "partial")))
            : BrowseFakeSilo.Json(Results(2, 2, (2, "partial"), (3, "none"))));
        var search = Create();

        await search.ActivateAsync("zzz");

        Assert.Equal(["Movie 1", "Movie 2", "Movie 3"], Titles(search));
        Assert.False(search.Requests.CanLoadMore);
    }

    [Fact]
    public async Task TitlesAddedLater_CanBeRequestedFromTheirCards()
    {
        Pages(totalPages: 2, perPage: 20);
        var search = Create();
        await search.ActivateAsync("heat");

        await search.Requests.LoadMoreCommand.ExecuteAsync(null);

        Assert.All(search.Requests.Cards, card => Assert.NotNull(card.RequestOffer));
        Assert.Equal(201, search.Requests.Cards[20].RequestOffer!.TmdbId);
    }

    [Fact]
    public async Task AFailedFirstPage_LeavesTheSectionOut()
    {
        _silo.Map(RequestSearchPath, _ => BrowseFakeSilo.Problem(502, "upstream_error"));
        var search = Create();

        await search.ActivateAsync("zzz");

        Assert.False(search.ShowsRequests);
        Assert.Equal("No titles found for \"zzz\"", search.StatusText);
        Assert.False(search.HasError);
    }

    [Fact]
    public async Task AFailedLaterRead_KeepsWhatShows_AndOffersTryAgain()
    {
        Pages(totalPages: 3, perPage: 20);
        var search = Create();
        await search.ActivateAsync("heat");
        _silo.Map(RequestSearchPath, _ => BrowseFakeSilo.Problem(502, "upstream_error"));

        await search.Requests.LoadMoreCommand.ExecuteAsync(null);

        Assert.True(search.ShowsRequests);
        Assert.True(search.Requests.HasMoreError);
        Assert.Equal(20, search.Requests.Cards.Count);
        Assert.False(search.Requests.ShowsLoadMore);
        Assert.False(search.Requests.LoadMoreCommand.CanExecute(null));

        Pages(totalPages: 3, perPage: 20);
        await search.Requests.TryAgainCommand.ExecuteAsync(null);

        Assert.False(search.Requests.HasMoreError);
        Assert.Equal(40, search.Requests.Cards.Count);
        Assert.Equal("2", PagesRead()[^1]);
    }

    [Fact]
    public async Task AFirstReadThatFailsOnALaterPage_KeepsWhatCame_AndTriesThatPageAgain()
    {
        _silo.Map(RequestSearchPath, request => Number(request, "page") == 1
            ? BrowseFakeSilo.Json(Results(1, 5, (1, "none"), (2, "none")))
            : BrowseFakeSilo.Problem(502, "upstream_error"));
        var search = Create();

        await search.ActivateAsync("borat");

        Assert.True(search.ShowsRequests);
        Assert.Equal(["Movie 1", "Movie 2"], Titles(search));
        Assert.True(search.Requests.HasMoreError);
        Assert.False(search.Requests.ShowsLoadMore);

        Pages(totalPages: 5, perPage: 20);
        await search.Requests.TryAgainCommand.ExecuteAsync(null);

        Assert.Equal(["1", "2", "2"], PagesRead());
        Assert.False(search.Requests.HasMoreError);
        Assert.Equal(22, search.Requests.Cards.Count);
    }

    [Fact]
    public async Task WhileTheFirstReadGoesOn_LoadMoreStartsNoSecond()
    {
        var heldPage = new TaskCompletionSource();
        _silo.Map(RequestSearchPath, async (request, _) =>
        {
            int page = Number(request, "page");
            if (page == 2)
            {
                await heldPage.Task;
            }
            return BrowseFakeSilo.Json(Results(page, 5, (page * 100 + 1, "none")));
        });
        var search = Create();

        var searching = search.ActivateAsync("borat");
        await WaitUntilAsync(() => PagesRead().Length == 2);
        Assert.False(search.Requests.LoadMoreCommand.CanExecute(null));
        await search.Requests.LoadMoreCommand.ExecuteAsync(null);
        heldPage.SetResult();
        await searching;

        Assert.Equal(["1", "2", "3"], PagesRead());
        Assert.Equal(["Movie 101", "Movie 201", "Movie 301"], Titles(search));
    }

    [Fact]
    public async Task PagesOfOnlyLibraryTitles_KeepTheSection_WhileTmdbHasMore()
    {
        _silo.Map(RequestSearchPath, request => BrowseFakeSilo.Json(Results(Number(request, "page"), 5, (Number(request, "page"), "available"))));
        var search = Create();

        await search.ActivateAsync("zzz");

        Assert.Equal(["1", "2", "3"], PagesRead());
        Assert.True(search.ShowsRequests);
        Assert.True(search.Requests.IsEmptySoFar);
        Assert.True(search.Requests.ShowsLoadMore);
    }

    [Fact]
    public async Task TheOnlyPage_OfOnlyLibraryTitles_LeavesTheSectionOut()
    {
        _silo.Map(RequestSearchPath, _ => BrowseFakeSilo.Json(Results(1, 1, (11, "available"))));
        var search = Create();

        await search.ActivateAsync("zzz");

        Assert.False(search.ShowsRequests);
        Assert.False(search.IsLibraryRescued);
        Assert.Equal("No titles found for \"zzz\"", search.StatusText);
    }

    [Fact]
    public async Task WhileTheLibraryFoundNothing_TheRingTurnsUntilTmdbAnswers()
    {
        var release = new TaskCompletionSource();
        _silo.Map(RequestSearchPath, async (_, _) =>
        {
            await release.Task;
            return BrowseFakeSilo.Json(Results(1, 0));
        });
        var search = Create();

        var searching = search.ActivateAsync("zzz");
        await WaitUntilAsync(() => search.Cards.Count == 0 && search.ResultsQuery == "zzz");

        Assert.True(search.IsSearching);
        Assert.Equal("", search.StatusText);
        release.SetResult();
        await searching;
        Assert.False(search.IsSearching);
        Assert.Equal("No titles found for \"zzz\"", search.StatusText);
    }

    [Fact]
    public async Task ALateAnswerForAnOlderText_IsDropped()
    {
        var heldAnswer = new TaskCompletionSource();
        _silo.Map(RequestSearchPath, async (request, _) =>
        {
            bool isFirst = Read(request, "q") == "first";
            if (isFirst)
            {
                await heldAnswer.Task;
            }
            return BrowseFakeSilo.Json(Results(1, 1, (isFirst ? 40 : 50, "none")));
        });
        var search = Create();
        await search.ActivateAsync();
        search.Query = "first";
        _time.Advance(SearchViewModel.DebounceDelay);
        var first = search.Searching;

        search.Query = "second";
        _time.Advance(SearchViewModel.DebounceDelay);
        await search.Searching;
        heldAnswer.SetResult();
        await first;

        Assert.Equal(["Movie 50"], Titles(search));
        Assert.Equal("second", search.Requests.Query);
        Assert.False(search.Requests.IsSearching);
    }

    [Fact]
    public async Task ANewText_DropsTheLaterReadOfTheLastOne()
    {
        Pages(totalPages: 3, perPage: 20);
        var search = Create();
        await search.ActivateAsync("first");
        var heldPage = new TaskCompletionSource();
        _silo.Map(RequestSearchPath, async (request, _) =>
        {
            if (Read(request, "q") == "first")
            {
                await heldPage.Task;
                return BrowseFakeSilo.Json(Results(2, 3, (999, "none")));
            }
            return BrowseFakeSilo.Json(Results(1, 1, (50, "none")));
        });
        var more = search.Requests.LoadMoreCommand.ExecuteAsync(null);
        Assert.True(search.Requests.IsLoadingMore);

        search.Query = "second";
        _time.Advance(SearchViewModel.DebounceDelay);
        await search.Searching;
        heldPage.SetResult();
        await more;

        Assert.Equal(["Movie 50"], Titles(search));
        Assert.False(search.Requests.IsLoadingMore);
        Assert.False(search.Requests.CanLoadMore);
    }

    [Fact]
    public async Task ACard_OpensTheTitlesPage_OrTheLibrarysTitle()
    {
        _silo.Map(RequestSearchPath, _ => BrowseFakeSilo.Json(Results(1, 1, (10, "none"), (12, "partial")).Replace(
            "\"tmdb_id\": 12, \"media_type\": \"movie\"", "\"tmdb_id\": 12, \"library_content_id\": \"movie:m12\", \"media_type\": \"movie\"",
            StringComparison.Ordinal)));
        var search = Create();
        await search.ActivateAsync("zzz");

        search.Requests.OpenCardCommand.Execute(search.Requests.Cards[0]);
        search.Requests.OpenCardCommand.Execute(search.Requests.Cards[1]);

        Assert.Equal(2, _navigation.Navigations.Count);
        Assert.Equal((AppRoute.Title, (object?)new TitleRouteParameter("movie", 10, "Movie 10")), _navigation.Navigations[0]);
        Assert.Equal((AppRoute.Item, (object?)"movie:m12"), _navigation.Navigations[1]);
    }

    [Fact]
    public async Task ClearingTheBox_HidesTheSection()
    {
        var search = Create();
        await search.ActivateAsync("zzz");

        search.ClearCommand.Execute(null);

        Assert.False(search.ShowsRequests);
        Assert.Empty(search.Requests.Cards);
        Assert.Equal("", search.Requests.Note);
    }

    [Fact]
    public async Task AnotherProfile_SearchesTmdbAgainAsIt()
    {
        var search = Create();
        await search.ActivateAsync("zzz");

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await search.Searching;

        Assert.Equal(2, _silo.To(StatusPath).Count);
        Assert.Equal(2, _silo.To(RequestSearchPath).Count);
        Assert.True(search.ShowsRequests);
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
