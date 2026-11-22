using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

/// <summary>Search's empty page and its states, in the words of the web's pages/SearchResults.tsx and pages/Catalog.tsx.</summary>
public sealed class SearchStatesTests
{
    private const string CatalogPath = "api/v2/catalog";
    private const string PeoplePath = "api/v2/catalog/people";

    private readonly BrowseFakeSilo _silo = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly RecordingNavigation _navigation = new();
    private readonly ManualTimeProvider _time = new();

    public SearchStatesTests()
    {
        _silo.Map("api/v2/catalog/search/capabilities", SearchCapabilities(people: true));
        _silo.Map(PeoplePath, People(("7", "Sample Person")));
        _silo.Map(CatalogPath, Page(null, Item("t1")));
        _silo.Map(SearchRequestToAddTests.StatusPath, """{ "requests_enabled": false }""");
    }

    private SearchViewModel Create() => SearchRequestToAddTests.Create(_silo, _session, _navigation, _time);

    [Fact]
    public async Task NothingTyped_ShowsTheEmptyPage_AndAResultLeavesIt()
    {
        var search = Create();
        await search.ActivateAsync();
        Assert.True(search.IsEmptySearch);

        await search.ActivateAsync("heat");

        Assert.False(search.IsEmptySearch);

        search.ClearCommand.Execute(null);

        Assert.True(search.IsEmptySearch);
    }

    [Fact]
    public async Task AFailedPeopleSearch_SaysSo_AndRetryPeopleSearchesAgain()
    {
        _silo.Map(PeoplePath, _ => BrowseFakeSilo.Problem(500, "internal_error"));
        var search = Create();
        await search.ActivateAsync("heat");

        Assert.True(search.HasPeopleError);
        Assert.True(search.ShowsPeopleSection);
        Assert.Equal(["t1"], search.Cards.Select(c => c.Id));

        _silo.Map(PeoplePath, People(("7", "Sample Person")));
        await search.RetryPeopleCommand.ExecuteAsync(null);

        Assert.False(search.HasPeopleError);
        Assert.Equal("Sample Person", Assert.Single(search.People).Name);
    }

    [Fact]
    public async Task AFailedSearch_SaysWhatTheWebSays()
    {
        _silo.Map(CatalogPath, _ => BrowseFakeSilo.Problem(500, "internal_error"));
        var search = Create();

        await search.ActivateAsync("heat");

        Assert.True(search.HasError);
        Assert.True(search.ShowsErrorCard);
        Assert.Equal("The search request failed. Please retry.", search.ErrorBody);
        Assert.False(search.ShowsNothingFound);
    }

    [Fact]
    public async Task ABusySearch_SaysItIsBusy_UnderTheWebsTitle()
    {
        _silo.Map(CatalogPath, _ => BrowseFakeSilo.Problem(503, Granary.Core.Api.ProblemCodes.DependencyUnavailable, TimeSpan.FromSeconds(1)));
        var search = Create();

        await search.ActivateAsync("heat");

        Assert.True(search.ShowsErrorCard);
        Assert.Equal("Search is busy, try again in a moment", search.ErrorBody);
    }

    [Fact]
    public async Task NothingAtAll_SaysNoItemsFound_Centred()
    {
        _silo.Map(CatalogPath, Page(null));
        _silo.Map(PeoplePath, People());
        var search = Create();

        await search.ActivateAsync("zzz");

        Assert.True(search.ShowsNothingFound);
        Assert.Equal("No items found.", search.StatusText);
    }
}
