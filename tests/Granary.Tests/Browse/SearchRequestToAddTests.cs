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
        _silo.Map(RequestSearchPath, request => BrowseFakeSilo.Json(Results(Number(request, "page"), 3, (10, "none"), (11, "available"), (12, "partial"))));
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

    private static int Number(HttpRequestMessage request, string name) => int.Parse(Read(request, name) ?? "0", System.Globalization.CultureInfo.InvariantCulture);

    private static string? Read(HttpRequestMessage request, string name) => new BrowseRequest(request.Method, request.RequestUri!, null).Query(name);

    private static string[] Titles(SearchViewModel search) => [.. search.Requests.Cards.Select(c => c.Title)];

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
        Assert.Equal("Page 1 of 3", search.Requests.PagePosition);
        Assert.True(search.Requests.ShowsPager);
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

    [Fact]
    public async Task ThePager_GoesForwardAndBack_AndStopsAtTheLastPage()
    {
        var search = Create();
        await search.ActivateAsync("zzz");
        Assert.False(search.Requests.PreviousPageCommand.CanExecute(null));

        await search.Requests.NextPageCommand.ExecuteAsync(null);
        await search.Requests.NextPageCommand.ExecuteAsync(null);

        Assert.Equal(["1", "2", "3"], _silo.To(RequestSearchPath).Select(r => r.Query("page")));
        Assert.Equal("Page 3 of 3", search.Requests.PagePosition);
        Assert.False(search.Requests.NextPageCommand.CanExecute(null));

        await search.Requests.PreviousPageCommand.ExecuteAsync(null);

        Assert.Equal("Page 2 of 3", search.Requests.PagePosition);
        Assert.Equal("2", _silo.To(RequestSearchPath)[^1].Query("page"));
    }

    [Fact]
    public async Task WhileAnotherPageLoads_TheShownOneStays_AndThePagerWaits()
    {
        var release = new TaskCompletionSource();
        var search = Create();
        await search.ActivateAsync("zzz");
        _silo.Map(RequestSearchPath, async (_, _) =>
        {
            await release.Task;
            return BrowseFakeSilo.Json(Results(2, 3, (20, "none")));
        });

        var next = search.Requests.NextPageCommand.ExecuteAsync(null);

        Assert.True(search.Requests.IsChangingPage);
        Assert.Equal(["Movie 10", "Movie 12"], Titles(search));
        Assert.False(search.Requests.PreviousPageCommand.CanExecute(null));
        release.SetResult();
        await next;
        Assert.False(search.Requests.IsChangingPage);
        Assert.Equal(["Movie 20"], Titles(search));
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
    public async Task AFailedLaterPage_SaysSo_AndOffersRetryAndBackToPageOne()
    {
        var search = Create();
        await search.ActivateAsync("zzz");
        _silo.Map(RequestSearchPath, _ => BrowseFakeSilo.Problem(502, "upstream_error"));

        await search.Requests.NextPageCommand.ExecuteAsync(null);

        Assert.True(search.ShowsRequests);
        Assert.True(search.Requests.HasPageError);
        Assert.Equal("Couldn’t load page 2.", search.Requests.PageErrorText);
        Assert.Equal("Page 2", search.Requests.PagePosition);
        Assert.Empty(search.Requests.Cards);
        Assert.False(search.Requests.IsPageEmpty);
        Assert.True(search.Requests.PreviousPageCommand.CanExecute(null));
        Assert.False(search.Requests.NextPageCommand.CanExecute(null));

        _silo.Map(RequestSearchPath, request => BrowseFakeSilo.Json(Results(Number(request, "page"), 3, (30, "none"))));
        await search.Requests.RetryPageCommand.ExecuteAsync(null);

        Assert.False(search.Requests.HasPageError);
        Assert.Equal("Page 2 of 3", search.Requests.PagePosition);
        Assert.Equal(["Movie 30"], Titles(search));

        await search.Requests.FirstPageCommand.ExecuteAsync(null);
        Assert.Equal("1", _silo.To(RequestSearchPath)[^1].Query("page"));
    }

    [Fact]
    public async Task APageOfOnlyLibraryTitles_StaysWhileTmdbHasOtherPages()
    {
        _silo.Map(RequestSearchPath, _ => BrowseFakeSilo.Json(Results(1, 2, (11, "available"))));
        var search = Create();

        await search.ActivateAsync("zzz");

        Assert.True(search.ShowsRequests);
        Assert.True(search.Requests.IsPageEmpty);
        Assert.True(search.Requests.NextPageCommand.CanExecute(null));
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
    public async Task APerson_HidesTheSection_AndBackShowsItAgain()
    {
        _silo.Map("api/v2/catalog/search/capabilities", SearchCapabilities(people: true));
        _silo.Map("api/v2/catalog/people", People(("7", "Al Pacino")));
        var search = Create();
        await search.ActivateAsync("al");

        await search.OpenPersonCommand.ExecuteAsync(search.People[0]);
        Assert.False(search.ShowsRequests);

        search.BackToResultsCommand.Execute(null);
        Assert.True(search.ShowsRequests);
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
