using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

/// <summary>Search's Box sets, as the web's search lists them ahead of people and titles.</summary>
public sealed class SearchBoxSetsTests
{
    private const string CatalogPath = "api/v2/catalog";
    private const string PeoplePath = "api/v2/catalog/people";
    private const string CapabilitiesPath = "api/v2/catalog/search/capabilities";
    private const string BoxSetsPath = "api/v2/catalog/box-sets";

    private readonly BrowseFakeSilo _silo = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly ManualTimeProvider _time = new();

    public SearchBoxSetsTests()
    {
        _silo.Map(CapabilitiesPath, SearchCapabilities(people: false, boxSets: true));
        _silo.Map(CatalogPath, Page(null, Item("movie-tmdb-1001")));
        _silo.Map(BoxSetsPath, BoxSetPayloads.SearchList);
        _silo.Map(SearchRequestToAddTests.StatusPath, """{ "requests_enabled": false }""");
    }

    private SearchViewModel Create() => SearchRequestToAddTests.Create(_silo, BrowseFakeSilo.ReadySession(), _navigation, _time);

    [Fact]
    public async Task MatchingBoxSets_ShowAsCards()
    {
        var search = await SearchAsync("masterplan");

        var request = Assert.Single(_silo.To(BoxSetsPath));
        Assert.Equal("masterplan", request.Query("q"));
        Assert.Equal("5", request.Query("limit"));
        Assert.True(search.ShowsBoxSets);
        Assert.Equal(["Masterplan Box Set", "Groundwork Collection"], search.BoxSets.Select(c => c.Title));
        var masterplan = search.BoxSets[0];
        // The web's BoxSetCard: watched progress once started, else the years, else the film count.
        Assert.Equal("1 / 3 WATCHED", masterplan.Subtitle);
        Assert.Equal("3 films", masterplan.BoxSetFilms);
        Assert.Equal(1.0 / 3, masterplan.Progress);
        Assert.Equal(new Uri("https://silo.test/media/api/v2/artwork/box-sets/1241/poster/w342.webp?exp=1&sig=bp"), masterplan.ImageUrl);
        Assert.Equal("Masterplan Box Set, box set of 3 films, 1 / 3 WATCHED", masterplan.AccessibleName);
        Assert.Equal("2019", search.BoxSets[1].Subtitle);
        Assert.Null(search.BoxSets[1].Progress);
        Assert.Equal(["movie-tmdb-1001"], search.Cards.Select(c => c.Id));
    }

    [Fact]
    public async Task ABoxSetCard_OpensTheBoxSetsPage()
    {
        var search = await SearchAsync("masterplan");

        search.OpenBoxSetCommand.Execute(search.BoxSets[1]);

        Assert.Equal((AppRoute.BoxSet, (object?)new BoxSetRouteParameter("88", "4", "Groundwork Collection")), Assert.Single(_navigation.Navigations));
    }

    [Fact]
    public async Task AServerWithoutBoxSets_IsNotAsked()
    {
        _silo.Map(CapabilitiesPath, SearchCapabilities(people: false));

        var search = await SearchAsync("masterplan");

        Assert.Empty(_silo.To(BoxSetsPath));
        Assert.False(search.ShowsBoxSets);
    }

    [Fact]
    public async Task Series_LeaveBoxSetsOut()
    {
        var search = Create();
        search.Scope = SearchScope.Series;

        await search.ActivateAsync("masterplan");

        Assert.Empty(_silo.To(BoxSetsPath));
        Assert.Empty(search.BoxSets);
    }

    [Fact]
    public async Task Movies_KeepBoxSets_AndSeriesThenDropThem()
    {
        var search = Create();
        search.Scope = SearchScope.Movies;
        await search.ActivateAsync("masterplan");
        Assert.Equal(2, search.BoxSets.Count);

        search.Scope = SearchScope.Series;
        await search.Searching;

        Assert.Empty(search.BoxSets);
    }

    [Fact]
    public async Task AFailedBoxSetSearch_LeavesTheTitles()
    {
        _silo.Map(BoxSetsPath, _ => BrowseFakeSilo.Problem(500, "internal_error"));

        var search = await SearchAsync("masterplan");

        Assert.Empty(search.BoxSets);
        Assert.Equal(["movie-tmdb-1001"], search.Cards.Select(c => c.Id));
        Assert.False(search.HasError);
    }

    [Fact]
    public async Task ASlowBoxSetSearch_DoesNotHoldTheTitles()
    {
        var never = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken read = default;
        _silo.Map(BoxSetsPath, (_, cancellationToken) =>
        {
            read = cancellationToken;
            sent.TrySetResult();
            return never.Task;
        });
        var search = Create();
        var searching = search.ActivateAsync("masterplan");

        // The cancellation asserted below needs the box set request out before the grace ends: a grace that ended while
        // the capabilities were still read would cancel the read before it sent anything. So wait for it to go out.
        await sent.Task.WaitAsync(TestContext.Current.CancellationToken);
        _time.Advance(SearchViewModel.BoxSetGrace);
        await searching;

        Assert.Equal(["movie-tmdb-1001"], search.Cards.Select(c => c.Id));
        Assert.Empty(search.BoxSets);
        // Given up on with the grace, not left running.
        Assert.True(read.IsCancellationRequested);
    }

    [Fact]
    public async Task BoxSetsAnsweringAfterTheTitles_WithinTheGrace_StillShow()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.Map(BoxSetsPath, async (_, _) =>
        {
            await answer.Task;
            return BrowseFakeSilo.Json(BoxSetPayloads.SearchList);
        });
        var search = Create();
        var searching = search.ActivateAsync("masterplan");

        _time.Advance(SearchViewModel.BoxSetGrace - TimeSpan.FromMilliseconds(500));
        answer.SetResult();
        await searching;

        Assert.Equal(2, search.BoxSets.Count);
        Assert.Equal(["movie-tmdb-1001"], search.Cards.Select(c => c.Id));
    }

    [Fact]
    public async Task ABoxSetAnswerForAnOlderText_IsDropped()
    {
        var older = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.Map(BoxSetsPath, async (request, _) =>
        {
            string? q = new BrowseRequest(request.Method, request.RequestUri!, null).Query("q");
            if (q == "lantern")
            {
                // The older answer comes late whatever happens to its request.
                await older.Task;
                return BrowseFakeSilo.Json("""{ "items": [ { "id": "7", "library_id": "3", "title": "Lantern Box Set", "movie_count": 2, "played_count": 0 } ] }""");
            }
            return BrowseFakeSilo.Json(BoxSetPayloads.SearchList);
        });
        var search = Create();
        var first = search.ActivateAsync("lantern");

        search.Query = "masterplan";
        await search.SearchNowCommand.ExecuteAsync(null);
        older.SetResult();
        await first;

        Assert.Equal("masterplan", search.ResultsQuery);
        Assert.Equal(["Masterplan Box Set", "Groundwork Collection"], search.BoxSets.Select(c => c.Title));
    }

    [Fact]
    public async Task UnreadableCapabilities_AskForNoBoxSets()
    {
        _silo.Map(CapabilitiesPath, _ => BrowseFakeSilo.Problem(500, "internal_error"));

        var search = await SearchAsync("masterplan");

        Assert.Empty(_silo.To(BoxSetsPath));
        Assert.Empty(search.BoxSets);
        Assert.Equal(["movie-tmdb-1001"], search.Cards.Select(c => c.Id));
    }

    [Fact]
    public async Task BoxSetsOff_NoTypeAsksForThem()
    {
        _silo.Map(CapabilitiesPath, SearchCapabilities(people: false, boxSets: false));
        var search = Create();
        search.Scope = SearchScope.Movies;
        await search.ActivateAsync("masterplan");

        search.Scope = SearchScope.MoviesAndSeries;
        await search.Searching;

        Assert.Equal(2, _silo.To(CatalogPath).Count);
        Assert.Empty(_silo.To(BoxSetsPath));
        Assert.Empty(search.BoxSets);
    }

    [Fact]
    public async Task ClearingTheBox_ClearsTheBoxSets()
    {
        var search = await SearchAsync("masterplan");

        search.Query = "";

        Assert.Empty(search.BoxSets);
        Assert.False(search.ShowsBoxSets);
    }

    private async Task<SearchViewModel> SearchAsync(string text)
    {
        var search = Create();
        await search.ActivateAsync(text);
        return search;
    }
}
