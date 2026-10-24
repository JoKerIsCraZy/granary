using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

public sealed class SearchViewModelTests
{
    private const string CatalogPath = "api/v2/catalog";
    private const string PeoplePath = "api/v2/catalog/people";
    private const string CapabilitiesPath = "api/v2/catalog/search/capabilities";

    private readonly BrowseFakeSilo _silo = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly RecordingNavigation _navigation = new();
    private readonly ManualTimeProvider _time = new();

    public SearchViewModelTests()
    {
        _silo.Map(CapabilitiesPath, SearchCapabilities(people: true));
        _silo.Map(PeoplePath, People(("7", "Al Pacino")));
        // Titles: one card per search text, so each answer shows which text it was for.
        _silo.Map(CatalogPath, request => BrowseFakeSilo.Json(Page(null, Item("for-" + Read(request, "q")))));
    }

    private SearchViewModel Create() =>
        new(_session, _silo.Catalog, _silo.Images, _navigation, new ImmediateDispatcher(), _time, _silo.Logs.For<SearchViewModel>());

    private static string? Read(HttpRequestMessage request, string name) => new BrowseRequest(request.Method, request.RequestUri!, null).Query(name);

    [Fact]
    public async Task Typing_SearchesOncePausedFor300Milliseconds()
    {
        var search = Create();
        await search.ActivateAsync();

        search.Query = "h";
        _time.Advance(TimeSpan.FromMilliseconds(200));
        search.Query = "he";
        _time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Empty(_silo.To(CatalogPath));

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await search.Searching;

        var request = Assert.Single(_silo.To(CatalogPath));
        Assert.Equal("he", request.Query("q"));
        Assert.Equal("video", request.Query("type"));
        Assert.Equal("60", request.Query("limit"));
        // Relevance is the server's order for a search; an explicit sort=relevance would be ascending.
        Assert.Null(request.Query("sort"));
        Assert.Equal(["for-he"], search.Cards.Select(c => c.Id));
        Assert.Equal("Results for \"he\"", search.Heading);
    }

    [Fact]
    public async Task SpaceAtTheEnd_DoesNotSearchAgain()
    {
        var search = Create();
        await search.ActivateAsync("heat");

        search.Query = "heat ";
        _time.Advance(SearchViewModel.DebounceDelay);
        await search.Searching;

        Assert.Single(_silo.To(CatalogPath));
        Assert.Equal(["for-heat"], search.Cards.Select(c => c.Id));
    }

    [Fact]
    public async Task NewText_CancelsTheSearchInFlightAndDropsItsLateAnswer()
    {
        var heldAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken heldToken = default;
        _silo.Map(CatalogPath, async (request, cancellationToken) =>
        {
            string? q = Read(request, "q");
            if (q == "heat")
            {
                heldToken = cancellationToken;
                await heldAnswer.Task;
            }
            return BrowseFakeSilo.Json(Page(null, Item("for-" + q)));
        });
        var search = Create();
        await search.ActivateAsync();
        search.Query = "heat";
        _time.Advance(SearchViewModel.DebounceDelay);
        var first = search.Searching;

        search.Query = "alien";
        _time.Advance(SearchViewModel.DebounceDelay);
        await search.Searching;
        heldAnswer.SetResult();
        await first;

        Assert.True(heldToken.IsCancellationRequested);
        Assert.Equal(["for-alien"], search.Cards.Select(c => c.Id));
        Assert.Equal("alien", search.ResultsQuery);
        Assert.False(search.IsSearching);
    }

    [Fact]
    public async Task People_AreSearchedAlongsideWhenTheServerCan()
    {
        var search = Create();
        await search.ActivateAsync("al");

        var request = Assert.Single(_silo.To(PeoplePath));
        Assert.Equal("al", request.Query("q"));
        Assert.Equal("20", request.Query("limit"));
        Assert.Equal("video", request.Query("media_scope"));
        var person = Assert.Single(search.People);
        Assert.Equal(new SearchPerson("7", "Al Pacino", new Uri("https://silo.test/media/api/v2/artwork/people/7.webp?sig=p")), person);
        Assert.Equal("AP", person.Initials);
        Assert.True(search.ShowsPeople);
    }

    [Fact]
    public async Task People_AreNotSearchedWhenTheServerCannot()
    {
        _silo.Map(CapabilitiesPath, SearchCapabilities(people: false));
        var search = Create();

        await search.ActivateAsync("al");

        Assert.Empty(_silo.To(PeoplePath));
        Assert.Empty(search.People);
        Assert.Equal(["for-al"], search.Cards.Select(c => c.Id));
    }

    [Fact]
    public async Task FailedPeopleSearch_LeavesTheTitles()
    {
        _silo.Map(PeoplePath, _ => BrowseFakeSilo.Problem(500, "internal_error"));
        var search = Create();

        await search.ActivateAsync("al");

        Assert.Empty(search.People);
        Assert.Equal(["for-al"], search.Cards.Select(c => c.Id));
        Assert.False(search.HasError);
    }

    [Fact]
    public async Task FailedPeopleSearch_IsAskedAgainNextTime()
    {
        _silo.Map(PeoplePath, _ => BrowseFakeSilo.Problem(500, "internal_error"));
        var search = Create();
        await search.ActivateAsync("al");
        Assert.Empty(search.People);

        _silo.Map(PeoplePath, People(("7", "Al Pacino"), ("", "Nobody")));
        search.Scope = SearchScope.Movies;
        await search.Searching;

        Assert.Equal(2, _silo.To(PeoplePath).Count);
        Assert.Equal("Al Pacino", Assert.Single(search.People).Name);
    }

    [Fact]
    public async Task ImageSizesThatTimeOut_StillSearchAtTheServerDefaultSizes()
    {
        _silo.Map("api/v2/images/capabilities", (_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout")));
        var search = Create();
        await search.ActivateAsync();
        search.Query = "heat";

        await search.SearchNowCommand.ExecuteAsync(null);

        Assert.Equal(["for-heat"], search.Cards.Select(c => c.Id));
        Assert.Null(Assert.Single(_silo.To(CatalogPath)).Query("poster_width"));
        Assert.False(search.HasError);
    }

    [Fact]
    public async Task Capabilities_AreReadOncePerSession()
    {
        var search = Create();
        await search.ActivateAsync("al");
        await search.ActivateAsync("pacino");

        Assert.Single(_silo.To(CapabilitiesPath));
        Assert.Equal(2, _silo.To(PeoplePath).Count);
    }

    [Fact]
    public async Task SearchDeadline_SaysSearchIsBusyAndRetrySearchesAgain()
    {
        _silo.Map(CatalogPath, _ => BrowseFakeSilo.Problem(503, ProblemCodes.DependencyUnavailable, TimeSpan.FromSeconds(1)));
        var search = Create();

        await search.ActivateAsync("heat");

        Assert.True(search.HasError);
        Assert.Equal("Search is busy, try again in a moment", search.StatusText);

        _silo.Map(CatalogPath, request => BrowseFakeSilo.Json(Page(null, Item("for-" + Read(request, "q")))));
        await search.RetryCommand.ExecuteAsync(null);

        Assert.False(search.HasError);
        Assert.Equal(["for-heat"], search.Cards.Select(c => c.Id));
        Assert.Equal("", search.StatusText);
    }

    [Fact]
    public async Task StaleCursor_RestartsTheQuery()
    {
        _silo.Map(CatalogPath, request => Read(request, "cursor") is null
            ? BrowseFakeSilo.Json(Page("c1", Item("m1"), Item("m2")))
            : BrowseFakeSilo.Problem(400, ProblemCodes.InvalidCursor));
        var search = Create();
        await search.ActivateAsync("heat");

        await search.LoadMoreCommand.ExecuteAsync(null);

        var requests = _silo.To(CatalogPath);
        Assert.Equal([null, "c1", null], requests.Select(r => r.Query("cursor")));
        Assert.All(requests, r => Assert.Equal("heat", r.Query("q")));
        Assert.Equal(["m1", "m2"], search.Cards.Select(c => c.Id));
        Assert.False(search.HasError);
    }

    [Fact]
    public async Task MorePages_RepeatTheQueryWithTheCursor()
    {
        _silo.Map(CatalogPath, request => Read(request, "cursor") is null
            ? BrowseFakeSilo.Json(Page("c1", Item("m1")))
            : BrowseFakeSilo.Json(Page(null, Item("m2"))));
        var search = Create();
        await search.ActivateAsync("heat");

        await search.LoadMoreCommand.ExecuteAsync(null);
        await search.LoadMoreCommand.ExecuteAsync(null);

        var requests = _silo.To(CatalogPath);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].Query("poster_width"), requests[1].Query("poster_width"));
        Assert.Equal(["m1", "m2"], search.Cards.Select(c => c.Id));
    }

    [Theory]
    [InlineData(SearchScope.Movies, "movie")]
    [InlineData(SearchScope.Series, "series")]
    public async Task TypeSwitch_SearchesAgainAtOnceWithItsType(SearchScope scope, string type)
    {
        var search = Create();
        await search.ActivateAsync("heat");

        search.Scope = scope;
        await search.Searching;

        var requests = _silo.To(CatalogPath);
        Assert.Equal(["video", type], requests.Select(r => r.Query("type")));
        // The same people match whatever the type.
        Assert.Single(_silo.To(PeoplePath));
        Assert.Single(search.People);
    }

    [Fact]
    public async Task Person_OpensTheirTitlesAndBackReturnsToTheResults()
    {
        var search = Create();
        await search.ActivateAsync("al");
        _silo.Map(CatalogPath, request => BrowseFakeSilo.Json(Read(request, "source") == "person"
            ? Page(null, Item("heat-1995"), Item("serpico-1973"))
            : Page(null, Item("for-" + Read(request, "q")))));

        await search.OpenPersonCommand.ExecuteAsync(search.People[0]);

        var request = _silo.To(CatalogPath)[^1];
        Assert.Equal("person", request.Query("source"));
        Assert.Equal("7", request.Query("person_id"));
        Assert.Equal("-year", request.Query("sort"));
        Assert.Null(request.Query("q"));
        Assert.Equal("Titles with Al Pacino", search.Heading);
        Assert.Equal(["heat-1995", "serpico-1973"], search.Cards.Select(c => c.Id));
        Assert.False(search.ShowsPeople);

        search.BackToResultsCommand.Execute(null);

        Assert.Equal("Results for \"al\"", search.Heading);
        Assert.Equal(["for-al"], search.Cards.Select(c => c.Id));
        Assert.True(search.ShowsPeople);
    }

    [Fact]
    public async Task Typing_LeavesAnOpenedPerson()
    {
        var search = Create();
        await search.ActivateAsync("al");
        await search.OpenPersonCommand.ExecuteAsync(search.People[0]);

        search.Query = "alien";

        Assert.Null(search.Person);
        _time.Advance(SearchViewModel.DebounceDelay);
        await search.Searching;
        Assert.Equal(["for-alien"], search.Cards.Select(c => c.Id));
    }

    [Fact]
    public async Task QueryParameter_SearchesWithoutWaiting()
    {
        var search = Create();

        await search.ActivateAsync("heat");

        Assert.Equal("heat", search.Query);
        Assert.Equal("heat", Assert.Single(_silo.To(CatalogPath)).Query("q"));
        Assert.Equal(TimeSpan.Zero, _time.Elapsed);
    }

    [Fact]
    public async Task Enter_SearchesWithoutWaiting()
    {
        var search = Create();
        await search.ActivateAsync();
        search.Query = "heat";

        await search.SearchNowCommand.ExecuteAsync(null);

        Assert.Equal(["for-heat"], search.Cards.Select(c => c.Id));
        _time.Advance(SearchViewModel.DebounceDelay);
        Assert.Single(_silo.To(CatalogPath));
    }

    [Fact]
    public async Task ClearingTheBox_ClearsTheResults()
    {
        var search = Create();
        await search.ActivateAsync("al");

        search.ClearCommand.Execute(null);

        Assert.Equal("Search", search.Heading);
        Assert.Empty(search.Cards);
        Assert.Empty(search.People);
        Assert.Equal("", search.StatusText);
    }

    [Fact]
    public async Task NoTitles_SaysSo()
    {
        _silo.Map(CatalogPath, Page(null));
        var search = Create();

        await search.ActivateAsync("zzz");

        Assert.Equal("No titles found for \"zzz\"", search.StatusText);
        Assert.False(search.HasError);
    }

    [Fact]
    public async Task Card_OpensTheItem()
    {
        var search = Create();
        await search.ActivateAsync("heat");

        search.OpenCardCommand.Execute(search.Cards[0]);

        Assert.Equal((AppRoute.Item, (object?)"for-heat"), Assert.Single(_navigation.Navigations));
    }

    [Fact]
    public async Task AnotherServer_StartsOverAndSearchesTheBoxThere()
    {
        var search = Create();
        await search.ActivateAsync("heat");

        _session.ActiveServer = BrowseFakeSilo.Server with { Id = "srv2" };
        _session.RaiseChanged();
        await search.Searching;

        Assert.Equal(2, _silo.To(CapabilitiesPath).Count);
        Assert.Equal(2, _silo.To(CatalogPath).Count);
        Assert.Equal(["for-heat"], search.Cards.Select(c => c.Id));
    }

    [Fact]
    public async Task NoProfile_AsksNothing()
    {
        _session.State = SessionState.ProfileRequired;
        var search = Create();

        await search.ActivateAsync("heat");

        Assert.Empty(_silo.Requests);
        Assert.Equal("Search", search.Heading);
    }
}
