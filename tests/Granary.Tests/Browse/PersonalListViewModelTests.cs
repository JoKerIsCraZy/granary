using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Catalog;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>Favorites, Watchlist, History and collections, as Silo's web catalog page lists them.</summary>
public sealed class PersonalListViewModelTests
{
    private const string CatalogPath = LibraryHarness.CatalogPath;

    private readonly LibraryHarness _harness = new();
    private readonly ViewerStateChanges _changes = new(new PlaybackStops());

    public PersonalListViewModelTests()
    {
        // The watchlist's second tab has its own tests (WatchlistTitlesViewModelTests); here the server keeps no such titles.
        _harness.Answer(WatchlistTitlesViewModelTests.StatusPath, """{ "requests_enabled": false }""");
        // Whose collection it is has its own tests (PersonalListCollectionTests); here it can't be read.
        _harness.Silo.On(HttpMethod.Get, "api/v2/collections/u1", ScriptedSilo.Problem(404, "not_found"));
    }

    private PersonalListViewModel Create() => WatchlistTitlesViewModelTests.CreateList(_harness, _changes);

    private string LastQuery => _harness.Queries(CatalogPath)[^1];

    [Theory]
    [InlineData("favorites", "Favorites", "Movies and shows you've marked as favorites.", "List Order")]
    [InlineData("watchlist", "Watchlist", "Things you've saved to watch later.", "List Order")]
    [InlineData("history", "History", "Everything you've recently watched.", "Watch History")]
    public async Task AList_ShowsInItsOwnOrder(string source, string title, string description, string order)
    {
        var list = Create();

        await list.ActivateAsync(source);

        Assert.Equal(title, list.Title);
        Assert.Equal(description, list.Description);
        Assert.Equal(order, list.SortLabel);
        Assert.False(list.IsOrderShown);
        Assert.StartsWith($"source={source}&limit=60&", LastQuery, StringComparison.Ordinal);
        Assert.Equal(["Alien", "Heat"], list.Grid.Cards.Select(c => c.Title));
    }

    [Fact]
    public async Task MediaAndSort_NarrowAndOrderTheList()
    {
        var list = Create();
        await list.ActivateAsync("favorites");

        await list.SetScopeCommand.ExecuteAsync(list.Scopes.Single(s => s.Label == "Movies"));
        Assert.StartsWith("source=favorites&type=movie&limit=60&", LastQuery, StringComparison.Ordinal);

        await list.SetSortCommand.ExecuteAsync(LibrarySortOption.Title);
        Assert.StartsWith("source=favorites&type=movie&sort=title&limit=60&", LastQuery, StringComparison.Ordinal);
        Assert.True(list.IsOrderShown);

        await list.SortDescendingCommand.ExecuteAsync(null);
        Assert.StartsWith("source=favorites&type=movie&sort=-title&limit=60&", LastQuery, StringComparison.Ordinal);

        await list.SetSortCommand.ExecuteAsync(null);
        Assert.StartsWith("source=favorites&type=movie&limit=60&", LastQuery, StringComparison.Ordinal);
        Assert.Equal("List Order", list.SortLabel);
    }

    [Fact]
    public async Task AnotherList_StartsInItsOwnOrderWithEveryKind()
    {
        var list = Create();
        await list.ActivateAsync("favorites");
        await list.SetScopeCommand.ExecuteAsync(list.Scopes.Single(s => s.Label == "Series"));

        await list.ActivateAsync("history");

        Assert.Equal("All Media", list.ScopeLabel);
        Assert.Equal("Watch History", list.SortLabel);
        Assert.StartsWith("source=history&limit=60&", LastQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameListAgain_KeepsItsPlace_UntilTheViewersStateChanged()
    {
        var list = Create();
        await list.ActivateAsync("watchlist");

        await list.ActivateAsync("watchlist");
        Assert.Single(_harness.Queries(CatalogPath));

        _changes.Report();
        await list.ActivateAsync("watchlist");
        Assert.Equal(2, _harness.Queries(CatalogPath).Count);
    }

    [Fact]
    public async Task ACollection_ShowsItsTitlesInItsOwnOrder()
    {
        var list = Create();

        await list.ShowCollectionAsync(new CollectionRouteParameter(CatalogSources.LibraryCollection, "s1", "Oscar Winners", "1"));

        Assert.Equal("Oscar Winners", list.Title);
        Assert.Equal("Refine the archive by type, era, rating, or genre.", list.Description);
        Assert.Equal("Collection Order", list.SortLabel);
        Assert.StartsWith("source=library_collection&library_id=1&collection_id=s1&limit=60&", LastQuery, StringComparison.Ordinal);

        await list.SetSortCommand.ExecuteAsync(LibrarySortOption.Title);
        Assert.StartsWith("source=library_collection&library_id=1&collection_id=s1&sort=title&limit=60&", LastQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APersonalCollection_IsNotBoundToALibrary()
    {
        var list = Create();

        await list.ShowCollectionAsync(new CollectionRouteParameter(CatalogSources.UserCollection, "u1", "Rainy days"));

        Assert.StartsWith("source=user_collection&collection_id=u1&limit=60&", LastQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameCollectionAgain_KeepsItsPlace_AndAListAfterItStartsAfresh()
    {
        var list = Create();
        var collection = new CollectionRouteParameter(CatalogSources.UserCollection, "u1", "Rainy days");
        await list.ShowCollectionAsync(collection);
        await list.SetScopeCommand.ExecuteAsync(list.Scopes.Single(s => s.Label == "Movies"));

        await list.ShowCollectionAsync(collection with { });
        Assert.Equal(2, _harness.Queries(CatalogPath).Count);
        Assert.Equal("Movies", list.ScopeLabel);

        await list.ActivateAsync("favorites");
        Assert.Equal("Favorites", list.Title);
        Assert.Equal("List Order", list.SortLabel);
        Assert.StartsWith("source=favorites&limit=60&", LastQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownList_ShowsNothing()
    {
        var list = Create();

        await list.ActivateAsync("downloads");
        await list.ActivateAsync(null);

        Assert.Empty(_harness.Queries(CatalogPath));
        Assert.Equal("", list.Title);
    }

    [Fact]
    public async Task ACard_OpensItsTitle_AndAPlaceholderNothing()
    {
        var list = Create();
        await list.ActivateAsync("favorites");

        list.OpenItemCommand.Execute(MediaCard.Placeholder);
        list.OpenItemCommand.Execute(list.Grid.Cards[1]);

        Assert.Equal((AppRoute.Item, (object?)"movie:heat-1995"), Assert.Single(_harness.Navigation.Navigations));
    }
}
