using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

/// <summary>The dimmed backdrop behind Search's header: the result the pointer or the keyboard is on, and nothing before.</summary>
public sealed class SearchBackdropTests
{
    private readonly BrowseFakeSilo _silo = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly ManualTimeProvider _time = new();

    public SearchBackdropTests()
    {
        _silo.Map("api/v2/catalog/search/capabilities", SearchCapabilities(people: false));
        _silo.Map("api/v2/catalog", Page(null, Item("heat"), Item("alien")));
        _silo.Map(SearchRequestToAddTests.StatusPath, """{ "requests_enabled": true }""");
        _silo.Map("api/v2/requests/search", """
            { "page": 1, "total_pages": 1, "total_results": 1,
              "results": [ { "tmdb_id": 10, "media_type": "movie", "title": "Movie 10", "year": 2020, "backdrop_path": "/m10-wide.jpg",
                             "availability": "none", "request": { "requestable": true } } ] }
            """);
    }

    private async Task<SearchViewModel> SearchedAsync(string text)
    {
        var search = SearchRequestToAddTests.Create(_silo, _session, new RecordingNavigation(), _time);
        await search.ActivateAsync(text);
        return search;
    }

    [Fact]
    public async Task NothingPointedAt_ShowsNoBackdrop()
    {
        var search = await SearchedAsync("he");

        Assert.NotEmpty(search.Cards);
        Assert.Null(search.Backdrop.Shown);
    }

    [Fact]
    public async Task ALibraryResultPointedAt_ShowsItsBackdrop_AndAnotherOnceItSettles()
    {
        var search = await SearchedAsync("he");

        search.PointCardCommand.Execute(search.Cards.Single(c => c.Id == "heat"));
        Assert.Equal("heat", search.Backdrop.Shown?.Key);

        search.PointCardCommand.Execute(search.Cards.Single(c => c.Id == "alien"));
        Assert.Equal("heat", search.Backdrop.Shown?.Key);
        _time.Advance(BrowseHeroViewModel.SettleTime);
        Assert.Equal("alien", search.Backdrop.Shown?.Key);
    }

    [Fact]
    public async Task ARequestToAddResultPointedAt_ShowsTmdbsBackdrop()
    {
        var search = await SearchedAsync("he");

        search.PointCardCommand.Execute(Assert.Single(search.Requests.Cards));

        Assert.Equal("tmdb:movie:10", search.Backdrop.Shown?.Key);
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w1280/m10-wide.jpg"), search.Backdrop.Shown?.BackdropUrl);
    }

    [Fact]
    public async Task ANewSearch_TakesTheBackdropAway()
    {
        var search = await SearchedAsync("he");
        search.PointCardCommand.Execute(search.Cards[0]);

        await search.ActivateAsync("al");

        Assert.Null(search.Backdrop.Shown);
    }
}
