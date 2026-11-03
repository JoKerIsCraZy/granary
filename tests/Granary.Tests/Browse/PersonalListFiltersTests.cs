using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Playback;
using Granary.Core.ViewModels;

namespace Granary.Tests.Browse;

/// <summary>Filters on Favorites, Watchlist, History and a collection: the library page's, with the list's own options.</summary>
public sealed class PersonalListFiltersTests
{
    private const string FiltersPath = "api/v2/catalog/filters";

    private readonly LibraryHarness _harness = new();

    public PersonalListFiltersTests()
    {
        _harness.Answer(WatchlistTitlesViewModelTests.StatusPath, """{ "requests_enabled": false }""");
        _harness.Answer(FiltersPath, LibraryPayloads.Filters);
    }

    private async Task<PersonalListViewModel> ShownAsync(string source)
    {
        var list = WatchlistTitlesViewModelTests.CreateList(_harness, new ViewerStateChanges(new PlaybackStops()));
        await list.ActivateAsync(source);
        await Auth.AuthPipeline.Eventually(() => list.Filters.GenreOptions.Count > 0);
        return list;
    }

    private string LastQuery => _harness.Queries(LibraryHarness.CatalogPath)[^1];

    [Fact]
    public async Task AList_AsksForItsOwnFilterOptions()
    {
        var list = await ShownAsync(CatalogSources.Favorites);

        Assert.StartsWith("source=favorites", _harness.Queries(FiltersPath)[^1], StringComparison.Ordinal);
        Assert.Equal(["Crime", "Drama"], list.Filters.GenreOptions.Select(o => o.Value));
        Assert.Equal(["PG-13", "R"], list.Filters.RatingOptions.Select(o => o.Value));
    }

    [Fact]
    public async Task AppliedFilters_NarrowTheList_AndClearFiltersWidensItAgain()
    {
        var list = await ShownAsync(CatalogSources.Watchlist);
        list.Filters.BeginEdit();
        list.Filters.DraftYearFrom = 2000;
        list.Filters.DraftYearTo = 1990;
        await list.Filters.ApplyCommand.ExecuteAsync(null);

        Assert.True(list.IsNarrowed);
        Assert.Contains("source=watchlist", LastQuery, StringComparison.Ordinal);
        Assert.Contains("year_min=1990&year_max=2000", LastQuery, StringComparison.Ordinal);

        await list.ClearFiltersCommand.ExecuteAsync(null);

        Assert.False(list.IsNarrowed);
        Assert.DoesNotContain("year_min=", LastQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuleFilters_GoToTheRuleQuery_WithTheListAsItsSource_InTheListsOwnOrder()
    {
        var list = await ShownAsync(CatalogSources.Watchlist);
        list.Filters.BeginEdit();
        list.Filters.GenreOptions.Single(o => o.Value == "Drama").IsSelected = true;
        list.Filters.VideoQualityOptions.Single(o => o.Label == "HDR").IsSelected = true;
        list.Filters.DraftWatchStatusIndex = (int)LibraryWatchStatus.Watched;
        list.Filters.DraftYearFrom = 1990;

        await list.Filters.ApplyCommand.ExecuteAsync(null);

        Assert.True(list.IsNarrowed);
        Assert.Equal(4, list.Filters.ActiveCount);
        Assert.Equal("""
            {"source":"watchlist","groups":[{"match":"all","rules":[{"field":"year","op":"gte","value":1990}]},{"match":"any","rules":[{"field":"genre","op":"contains","value":"Drama"}]},{"match":"all","rules":[{"field":"hdr","op":"is","value":true}]},{"match":"all","rules":[{"field":"watched","op":"is","value":true}]}],"limit":60}
            """, Assert.Single(_harness.QueryBodies()));
    }

    [Fact]
    public async Task AList_OffersItsOwnOrder_AndTheSortsItHad_WithTheWebsWords()
    {
        var list = await ShownAsync(CatalogSources.Favorites);

        Assert.Equal(["Title", "Date Added", "Release Date", "Year", "Duration", "IMDb Rating"], list.SortOptions.Select(o => o.Label));
    }

    [Fact]
    public async Task AnotherList_StartsUnfiltered_WithItsOwnOptions()
    {
        var list = await ShownAsync(CatalogSources.Favorites);
        list.Filters.BeginEdit();
        list.Filters.GenreOptions[0].IsSelected = true;
        await list.Filters.ApplyCommand.ExecuteAsync(null);

        await list.ActivateAsync(CatalogSources.History);
        await Auth.AuthPipeline.Eventually(() => list.Filters.GenreOptions.Count > 0);

        Assert.Equal(0, list.Filters.ActiveCount);
        Assert.StartsWith("source=history", _harness.Queries(FiltersPath)[^1], StringComparison.Ordinal);
        Assert.StartsWith("source=history", LastQuery, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Get, _harness.Silo.Sent[^1].Method);
    }

    [Fact]
    public async Task HistorysPicks_GoWhenAFilterChangesWhatShows_AndSelectingGoesOn()
    {
        var list = await ShownAsync(CatalogSources.History);
        list.StartSelectingCommand.Execute(null);
        list.Selection.Toggle(list.Grid.Cards[0]);
        Assert.Equal(1, list.Selection.Count);

        list.Filters.BeginEdit();
        list.Filters.GenreOptions[0].IsSelected = true;
        await list.Filters.ApplyCommand.ExecuteAsync(null);

        Assert.True(list.Selection.IsActive);
        Assert.Equal(0, list.Selection.Count);
    }

    [Fact]
    public async Task AnotherProfile_StartsTheSameListUnfiltered_WithItsOwnOptions()
    {
        var list = await ShownAsync(CatalogSources.Favorites);
        list.Filters.BeginEdit();
        list.Filters.GenreOptions[0].IsSelected = true;
        await list.Filters.ApplyCommand.ExecuteAsync(null);
        int asked = _harness.Queries(FiltersPath).Count;

        _harness.Session.ProfileId = "8";
        await list.ActivateAsync(CatalogSources.Favorites);

        Assert.Equal(0, list.Filters.ActiveCount);
        Assert.Equal(asked + 1, _harness.Queries(FiltersPath).Count);
        Assert.Single(_harness.QueryBodies());
    }

    [Fact]
    public async Task OptionsThatFailed_AreAskedAgainOnTheNextVisit()
    {
        // A refusal no retry repeats on its own (a 503 the request pipeline would ask again).
        _harness.Silo.On(HttpMethod.Get, FiltersPath, Profiles.ScriptedSilo.Problem(400, "invalid_request"),
            Profiles.ScriptedSilo.Problem(400, "invalid_request"), Profiles.ScriptedSilo.Json(System.Net.HttpStatusCode.OK, LibraryPayloads.Filters));
        var list = WatchlistTitlesViewModelTests.CreateList(_harness, new ViewerStateChanges(new PlaybackStops()));
        await list.ActivateAsync(CatalogSources.Favorites);
        await Auth.AuthPipeline.Eventually(() => list.Filters.HasOptionsError);

        await list.ActivateAsync(CatalogSources.Favorites);
        await Auth.AuthPipeline.Eventually(() => list.Filters.GenreOptions.Count > 0);

        Assert.False(list.Filters.HasOptionsError);
        // Each visit asks with the files' languages, and once more without them when that fails.
        Assert.Equal(3, _harness.Queries(FiltersPath).Count);
    }

    [Fact]
    public async Task TheOptions_FollowTheKindOfTitlesShown()
    {
        var list = await ShownAsync(CatalogSources.Watchlist);

        await list.SetScopeCommand.ExecuteAsync(ListMediaScope.All.Single(s => s.Label == "Movies"));

        Assert.Contains("type=movie", _harness.Queries(FiltersPath)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACollection_HasItsOwnOptions_AndFilters()
    {
        _harness.Silo.On(HttpMethod.Get, "api/v2/collections/u1", Profiles.ScriptedSilo.Problem(404, "not_found"));
        var list = WatchlistTitlesViewModelTests.CreateList(_harness, new ViewerStateChanges(new PlaybackStops()));

        await list.ShowCollectionAsync(new CollectionRouteParameter(CatalogSources.UserCollection, "u1", "Rainy days"));
        await Auth.AuthPipeline.Eventually(() => list.Filters.RatingOptions.Count > 0);
        list.Filters.BeginEdit();
        list.Filters.RatingOptions[1].IsSelected = true;
        await list.Filters.ApplyCommand.ExecuteAsync(null);

        Assert.StartsWith("source=user_collection&collection_id=u1", _harness.Queries(FiltersPath)[^1], StringComparison.Ordinal);
        Assert.Contains("content_rating=R", LastQuery, StringComparison.Ordinal);
    }
}
