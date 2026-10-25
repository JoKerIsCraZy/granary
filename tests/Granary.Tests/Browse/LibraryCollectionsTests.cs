using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.ViewModels;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

public sealed class LibraryCollectionsTests
{
    private const string CollectionsPath = "api/v2/library/1/collections";

    private readonly LibraryHarness _harness = new();

    private async Task<LibraryViewModel> OpenCollectionsTabAsync()
    {
        var library = _harness.Create();
        await library.ActivateAsync("1");
        await library.SelectTabCommand.ExecuteAsync(LibraryTab.Collections);
        return library;
    }

    [Fact]
    public async Task Shelves_FollowTheGroupsAndTheUngroupedInTheirOrder()
    {
        var library = await OpenCollectionsTabAsync();

        var shelves = library.Collections.Shelves;
        Assert.Equal(2, shelves.Count);
        Assert.Equal("Mine", shelves[0].Title);
        Assert.Equal("Rainy days", Assert.Single(shelves[0].Cards).Title);
        Assert.Equal("4 TITLES", shelves[0].Cards[0].Subtitle);
        Assert.False(shelves[1].HasTitle);
        var oscars = Assert.Single(shelves[1].Cards);
        Assert.Equal("Oscar Winners", oscars.Title);
        Assert.Equal("12 TITLES", oscars.Subtitle);
        Assert.Equal(new Uri("https://nas.example/silo/api/v2/artwork/collections/c1/poster.webp?exp=1&sig=s"), oscars.ImageUrl);
        Assert.Equal(LibraryLoadState.Ready, library.Collections.State);
    }

    [Fact]
    public async Task LibraryCollection_OpensItsTitlesInTheCollectionsOrder()
    {
        var library = await OpenCollectionsTabAsync();
        var collections = library.Collections;
        var oscars = collections.Shelves[1].Cards[0];

        await collections.OpenCommand.ExecuteAsync(oscars);

        Assert.True(collections.IsCollectionOpen);
        Assert.Equal("Oscar Winners", collections.OpenCollectionTitle);
        Assert.Equal($"source=library_collection&library_id=1&collection_id=c1&limit=60&{LibraryHarness.ImageQuery}",
            Assert.Single(_harness.Queries(LibraryHarness.CatalogPath)));
        Assert.Equal(2, collections.Items.Cards.Count);

        library.OpenItemCommand.Execute(collections.Items.Cards[1]);

        Assert.Equal((AppRoute.Item, (object?)"movie:heat-1995"), _harness.Navigation.Navigations[^1]);
    }

    [Fact]
    public async Task PersonalCollection_OpensWithoutTheLibrary()
    {
        var library = await OpenCollectionsTabAsync();

        await library.Collections.OpenCommand.ExecuteAsync(library.Collections.Shelves[0].Cards[0]);

        Assert.StartsWith("source=user_collection&collection_id=u1&limit=60&", Assert.Single(_harness.Queries(LibraryHarness.CatalogPath)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_ReturnsToTheShelves()
    {
        var library = await OpenCollectionsTabAsync();
        await library.Collections.OpenCommand.ExecuteAsync(library.Collections.Shelves[1].Cards[0]);

        library.Collections.CloseCommand.Execute(null);

        Assert.False(library.Collections.IsCollectionOpen);
        Assert.True(library.Collections.IsShelvesShown);
        Assert.Empty(library.Collections.Items.Cards);
        Assert.Equal(2, library.Collections.Shelves.Count);
    }

    [Fact]
    public async Task ServerWithoutGroups_ShowsTheCollectionsInFull()
    {
        _harness.Answer(CollectionsPath, LibraryPayloads.CollectionsWithoutGroups);

        var library = await OpenCollectionsTabAsync();

        var shelf = Assert.Single(library.Collections.Shelves);
        Assert.Null(shelf.Title);
        Assert.Equal(new[] { "Oscar Winners", "Heists" }, shelf.Cards.Select(c => c.Title));
        Assert.Equal("1 TITLE", shelf.Cards[1].Subtitle);
    }

    [Fact]
    public async Task CardWithoutAnId_IsLeftOut()
    {
        _harness.Answer(CollectionsPath, """
            { "library_id": "1", "collections": [], "groups": [],
              "ungrouped": { "sort_order": 0, "collections": [
                { "id": "", "title": "Broken", "poster_url": "", "item_count": 3, "poster_is_collage": false },
                { "id": "c1", "title": "Oscar Winners", "poster_url": "", "item_count": 12, "poster_is_collage": false } ] } }
            """);

        var library = await OpenCollectionsTabAsync();

        Assert.Equal("Oscar Winners", Assert.Single(Assert.Single(library.Collections.Shelves).Cards).Title);
    }

    [Fact]
    public async Task NoCollections_ShowsTheEmptyState()
    {
        _harness.Answer(CollectionsPath, LibraryPayloads.NoCollections);

        var library = await OpenCollectionsTabAsync();

        Assert.True(library.Collections.IsEmpty);
        Assert.Empty(library.Collections.Shelves);
    }

    [Fact]
    public async Task FailedCollections_ShowAFriendlyTextAndRetryRecovers()
    {
        _harness.Answer(CollectionsPath, ScriptedSilo.Problem(403, ProblemCodes.PermissionDenied),
            ScriptedSilo.Json(System.Net.HttpStatusCode.OK, LibraryPayloads.Collections));
        var library = await OpenCollectionsTabAsync();
        Assert.True(library.Collections.HasError);
        Assert.Equal(LibraryErrors.Denied, library.Collections.ErrorMessage);

        await library.Collections.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, library.Collections.Shelves.Count);
        Assert.False(library.Collections.HasError);
    }
}
