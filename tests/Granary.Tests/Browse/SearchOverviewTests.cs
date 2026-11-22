using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

/// <summary>
/// Search's overview and its views, as the web's pages/SearchResults.tsx: two rows of library results, the titles to request,
/// people and three more rows, with the whole library list and the whole request list each a view of its own.
/// </summary>
public sealed class SearchOverviewTests
{
    private const string CatalogPath = "api/v2/catalog";
    private const string PeoplePath = "api/v2/catalog/people";
    private const string RequestSearchPath = "api/v2/requests/search";

    private readonly BrowseFakeSilo _silo = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly RecordingNavigation _navigation = new();
    private readonly ManualTimeProvider _time = new();

    public SearchOverviewTests()
    {
        _silo.Map("api/v2/catalog/search/capabilities", SearchCapabilities(people: true));
        _silo.Map(PeoplePath, People(("7", "Sample Person")));
        _silo.Map(SearchRequestToAddTests.StatusPath, """{ "requests_enabled": true }""");
        Library(30, hasMore: false);
        Requests(totalPages: 1, perPage: 6);
    }

    private SearchViewModel Create()
    {
        var search = SearchRequestToAddTests.Create(_silo, _session, _navigation, _time);
        search.Columns = 4;
        return search;
    }

    // The library's first page: titles t1, t2, …, and a next page when hasMore.
    private void Library(int count, bool hasMore) =>
        _silo.Map(CatalogPath, request => BrowseFakeSilo.Json(Read(request, "cursor") is null
            ? Page(hasMore ? "c2" : null, [.. Enumerable.Range(1, count).Select(i => Item("t" + i))])
            : Page(null, Item("later"))));

    // TMDB's pages for the text, each with perPage titles to add, ids page × 100 + 1, 2, ….
    private void Requests(int totalPages, int perPage) =>
        _silo.Map(RequestSearchPath, request =>
        {
            int page = int.Parse(Read(request, "page") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
            return BrowseFakeSilo.Json(RequestPage(page, totalPages, [.. Enumerable.Range(1, perPage).Select(i => page * 100 + i)]));
        });

    private static string RequestPage(int page, int totalPages, int[] ids) =>
        $$"""
        { "page": {{page}}, "total_pages": {{totalPages}}, "total_results": {{ids.Length}},
          "results": [{{string.Join(",", ids.Select(id => $$"""
            { "tmdb_id": {{id}}, "media_type": "movie", "title": "Movie {{id}}", "year": 2020, "availability": "none",
              "in_watchlist": false, "request": { "requestable": true, "following": false, "requested_by_viewer": false } }
            """))}}] }
        """;

    private static string? Read(HttpRequestMessage request, string name) => new BrowseRequest(request.Method, request.RequestUri!, null).Query(name);

    private static string[] Ids(IEnumerable<Granary.Core.ViewModels.Cards.MediaCard> cards) => [.. cards.Select(c => c.Id)];

    private static string[] Range(int from, int to) => [.. Enumerable.Range(from, to - from + 1).Select(i => "t" + i)];

    [Fact]
    public async Task TheOverview_ShowsTwoRows_ThenTheTitlesToRequest_ThenPeople_ThenThreeMoreRows()
    {
        var search = Create();

        await search.ActivateAsync("heat");

        Assert.Equal(SearchView.Overview, search.ShownView);
        Assert.Equal(Range(1, 8), Ids(search.BestCards));
        Assert.Equal("Best matches", search.FirstRowsHeading);
        Assert.True(search.ShowsRequestRow);
        Assert.Equal(["Movie 101", "Movie 106"], new[] { 0, 5 }.Select(i => search.RequestRow[i].Title));
        Assert.True(search.ShowsPeopleAfterRequests);
        Assert.False(search.ShowsPeopleAfterMore);
        Assert.Equal(Range(9, 20), Ids(search.MoreCards));
        Assert.True(search.ShowsMoreHeading);
        // The rest is in the library view.
        Assert.True(search.ShowsAllResults);
        Assert.Equal("30", search.LibraryCountText);
    }

    [Fact]
    public async Task OtherColumns_CutTheSameFirstPageAgain()
    {
        var search = Create();
        await search.ActivateAsync("heat");

        search.Columns = 6;

        Assert.Equal(Range(1, 12), Ids(search.BestCards));
        Assert.Equal(Range(13, 30), Ids(search.MoreCards));
        Assert.False(search.ShowsAllResults);
    }

    [Fact]
    public async Task AFewTitles_FitTheFirstRows_AndOfferNoLibraryView()
    {
        Library(3, hasMore: false);
        var search = Create();

        await search.ActivateAsync("heat");

        Assert.Equal(Range(1, 3), Ids(search.BestCards));
        Assert.Empty(search.MoreCards);
        Assert.False(search.ShowsAllResults);
    }

    [Fact]
    public async Task AServerWithMorePages_OffersTheLibraryView_AndCountsAtLeastThePageShown()
    {
        Library(10, hasMore: true);
        var search = Create();

        await search.ActivateAsync("heat");

        Assert.True(search.ShowsAllResults);
        Assert.Equal("10+", search.LibraryCountText);
    }

    [Fact]
    public async Task Relevance_LeadsTheSortMenu_AndASearchSendsNoSort()
    {
        var search = Create();

        await search.ActivateAsync("heat");

        Assert.Null(search.Sort);
        Assert.Equal("Relevance", search.SortLabel);
        Assert.False(search.IsOrderShown);
        Assert.Equal(CatalogSortFields.Title, search.SortOptions[0].Field);
        // Not the library's Date Added default: the server ranks the text by relevance.
        Assert.Null(Assert.Single(_silo.To(CatalogPath)).Query("sort"));
    }

    [Fact]
    public async Task TheServersRatingSorts_AreOffered_WhileTheFilterOptionsLoadBeside()
    {
        _silo.Map("api/v2/capabilities/ratings", """{ "state": "available", "sources": [{ "source": "rt_critic", "name": "Rotten Tomatoes" }] }""");
        var search = Create();

        await search.ActivateAsync("heat");
        await WaitUntilAsync(() => search.SortOptions.Any(o => o.Field == CatalogSortFields.RatingRtCritic));

        Assert.Contains(search.SortOptions, o => o.Field == CatalogSortFields.RatingRtCritic);
    }

    [Fact]
    public async Task AnotherServerWhileShown_AsksForItsFilterOptions()
    {
        var search = Create();
        await search.ActivateAsync("heat");
        await WaitUntilAsync(() => _silo.To("api/v2/catalog/filters").Count > 0);
        int asked = _silo.To("api/v2/catalog/filters").Count;

        _session.ActiveServer = BrowseFakeSilo.Server with { Id = "srv2" };
        _session.RaiseChanged();
        await search.Searching;
        await WaitUntilAsync(() => _silo.To("api/v2/catalog/filters").Count > asked);

        Assert.True(_silo.To("api/v2/catalog/filters").Count > asked);
    }

    // The reads beside the search are not awaited by it; they end within a moment.
    private static async Task WaitUntilAsync(Func<bool> done)
    {
        for (int i = 0; i < 100 && !done(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task UnderAnotherSort_TheRowsReadAsOneList_WithTheRequestRowTwoRowsIn()
    {
        var search = Create();
        await search.ActivateAsync("heat");

        await search.SetSortCommand.ExecuteAsync(search.SortOptions.Single(o => o.Field == CatalogSortFields.Year));

        Assert.Equal("-year", _silo.To(CatalogPath)[^1].Query("sort"));
        Assert.Equal("In your library", search.FirstRowsHeading);
        Assert.Equal("Sorted by Year", search.SortedByText);
        Assert.True(search.IsSortedOtherwise);
        Assert.Equal(Range(1, 8), Ids(search.BestCards));
        Assert.True(search.ShowsRequestRow);
        Assert.Equal(Range(9, 20), Ids(search.MoreCards));
        Assert.False(search.ShowsMoreHeading);
        // People follow the list instead of splitting it.
        Assert.False(search.ShowsPeopleAfterRequests);
        Assert.True(search.ShowsPeopleAfterMore);
        // TMDB knows no sort: the titles to request are not asked for again.
        Assert.Single(_silo.To(RequestSearchPath));
    }

    [Fact]
    public async Task BackToRelevance_SendsNoSortAgain()
    {
        var search = Create();
        await search.ActivateAsync("heat");
        await search.SetSortCommand.ExecuteAsync(search.SortOptions.Single(o => o.Field == CatalogSortFields.Year));

        await search.BackToRelevanceCommand.ExecuteAsync(null);

        Assert.Null(_silo.To(CatalogPath)[^1].Query("sort"));
        Assert.Null(search.Sort);
        Assert.Equal("Best matches", search.FirstRowsHeading);
    }

    [Fact]
    public async Task Filters_NarrowTheSearch_AndKeepTheTitlesToRequest()
    {
        var search = Create();
        await search.ActivateAsync("heat");

        search.Filters.DraftYearFrom = 1990;
        await search.Filters.ApplyCommand.ExecuteAsync(null);

        var sent = _silo.To(CatalogPath)[^1];
        Assert.Equal(("heat", "1990"), (sent.Query("q"), sent.Query("year_min")));
        Assert.Single(_silo.To(RequestSearchPath));
    }

    [Fact]
    public async Task TheRequestView_ShowsTheWholeList_AndReadsOnAtItsEnd()
    {
        Requests(totalPages: 3, perPage: 20);
        var search = Create();
        await search.ActivateAsync("heat");
        Assert.True(search.ShowsRequestsTab);
        Assert.Equal("20+", search.RequestCountText);

        search.ShowViewCommand.Execute(SearchView.Requests);
        await search.Requests.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(SearchView.Requests, search.ShownView);
        Assert.Equal(40, search.Requests.Cards.Count);
        Assert.Equal(["1", "2"], _silo.To(RequestSearchPath).Select(r => r.Query("page")));
        // The overview's row keeps its twenty.
        Assert.Equal(20, search.RequestRow.Count);
        Assert.False(search.ShowsPeople);
    }

    [Fact]
    public async Task TheLibraryView_ShowsEveryTitle_AndReadsOn_WhileTheOverviewKeepsItsRows()
    {
        Library(30, hasMore: true);
        var search = Create();
        await search.ActivateAsync("heat");

        search.ShowViewCommand.Execute(SearchView.Library);
        await search.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(SearchView.Library, search.ShownView);
        Assert.Equal(31, search.Cards.Count);
        Assert.Equal("later", search.Cards[^1].Id);
        Assert.Equal(Range(9, 20), Ids(search.MoreCards));
        Assert.False(search.ShowsPeople);
    }

    [Fact]
    public async Task ANewText_ReturnsToTheOverview_AndKeepsTheSort()
    {
        var search = Create();
        await search.ActivateAsync("heat");
        await search.SetSortCommand.ExecuteAsync(search.SortOptions.Single(o => o.Field == CatalogSortFields.Year));
        search.ShowViewCommand.Execute(SearchView.Library);

        search.Query = "alien";
        await search.SearchNowCommand.ExecuteAsync(null);

        Assert.Equal(SearchView.Overview, search.ShownView);
        Assert.Equal(("alien", "-year"), (_silo.To(CatalogPath)[^1].Query("q"), _silo.To(CatalogPath)[^1].Query("sort")));
    }

    [Fact]
    public async Task AShortText_NeverSearchesTmdb_AndARequestViewFallsBackToTheOverview()
    {
        var search = Create();
        await search.ActivateAsync("heat");
        search.ShowViewCommand.Execute(SearchView.Requests);

        search.Query = "h";
        await search.SearchNowCommand.ExecuteAsync(null);

        Assert.Single(_silo.To(RequestSearchPath));
        Assert.False(search.ShowsRequestsTab);
        Assert.False(search.ShowsRequestRow);
        Assert.Equal(SearchView.Overview, search.ShownView);
    }

    [Fact]
    public async Task AnotherScope_ShowsNoneOfTheLastScopesRequestPages()
    {
        Requests(totalPages: 3, perPage: 20);
        _silo.Map(PeoplePath, People());
        var search = Create();
        await search.ActivateAsync("heat");
        search.ShowViewCommand.Execute(SearchView.Requests);
        await search.Requests.LoadMoreCommand.ExecuteAsync(null);
        // Series finds nothing to add, and the library nothing either.
        _silo.Map(RequestSearchPath, _ => BrowseFakeSilo.Json(RequestPage(1, 1, [])));
        Library(0, hasMore: false);

        search.Scope = SearchScope.Series;
        await search.Searching;

        Assert.Empty(search.Requests.Cards);
        Assert.Empty(search.RequestRow);
        Assert.False(search.ShowsRequestsTab);
        Assert.Equal(SearchView.Overview, search.ShownView);
        Assert.Equal("No items found.", search.StatusText);
    }

    [Fact]
    public async Task ALibraryThatFoundNothing_LetsTheTitlesToRequestStandIn()
    {
        Library(0, hasMore: false);
        var search = Create();

        await search.ActivateAsync("zzz");

        Assert.True(search.IsLibraryRescued);
        Assert.False(search.ShowsRequestRow);
        Assert.True(search.ShowsRequestGrid);
        Assert.Empty(search.BestCards);
    }

    [Fact]
    public async Task ReadingOnInTheOverviewsRequestGrid_KeepsTheOverview()
    {
        // The web's #4786: paging the overview's Request to add grid stays in the overview.
        Library(0, hasMore: false);
        Requests(totalPages: 3, perPage: 20);
        var search = Create();
        await search.ActivateAsync("zzz");

        await search.Requests.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(SearchView.Overview, search.ShownView);
        Assert.True(search.ShowsRequestGrid);
        Assert.Equal(40, search.Requests.Cards.Count);
    }

    [Fact]
    public async Task OnlyTheOverview_ShowsTheRequestGridWhenTheLibraryHasTitles()
    {
        var search = Create();
        await search.ActivateAsync("heat");
        Assert.False(search.ShowsRequestGrid);

        search.ShowViewCommand.Execute(SearchView.Requests);

        Assert.True(search.ShowsRequestGrid);
        Assert.False(search.ShowsRequestRow);
    }
}
