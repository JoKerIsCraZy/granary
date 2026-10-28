using System.Net;
using System.Text.Json;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Collections;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>"Add to a collection" from a title's page, as the web's dialog.</summary>
public sealed class AddToCollectionViewModelTests
{
    private const string ListPath = "api/v2/collections";

    /// <summary>Two own manual collections (Dark is in the first), an own synced list, and another profile's.</summary>
    private const string Collections = """
        { "items": [
            { "id": "c1", "name": "Ghibli Night", "collection_type": "manual", "creator_profile_id": "7", "item_count": 4, "is_shared": true, "contains": true },
            { "id": "c2", "name": "Rainy days", "collection_type": "manual", "creator_profile_id": "7", "item_count": 1, "contains": false },
            { "id": "c3", "name": "Top Picks", "collection_type": "mdblist", "creator_profile_id": "7", "item_count": 9 },
            { "id": "c4", "name": "Cartoons", "collection_type": "manual", "creator_profile_id": "8", "item_count": 2 }
          ] }
        """;

    private readonly LibraryHarness _harness = new();

    public AddToCollectionViewModelTests()
    {
        _harness.Answer(ListPath, Collections);
        _harness.Silo.On(HttpMethod.Put, "api/v2/collections/c2/items/series%3Adark", ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Delete, "api/v2/collections/c1/items/series%3Adark", ScriptedSilo.NoContent());
    }

    private async Task<AddToCollectionViewModel> OpenAsync()
    {
        var dialog = new AddToCollectionViewModel(_harness.Session, new CollectionsApi(_harness.Silo.Http), _harness.Clock,
            _harness.Logs.For<AddToCollectionViewModel>());
        await dialog.LoadAsync("series:dark", "Dark");
        return dialog;
    }

    [Fact]
    public async Task TheOwnManualCollections_ShowWithTheirTicks()
    {
        var dialog = await OpenAsync();

        Assert.True(dialog.HasCollections);
        Assert.Equal(["Ghibli Night", "Rainy days"], dialog.Shown.Select(r => r.Name));
        Assert.Equal([true, false], dialog.Shown.Select(r => r.IsIn));
        Assert.Equal("Manual \u00B7 4 titles", dialog.Shown[0].Meta);
        Assert.True(dialog.Shown[0].IsShared);
        Assert.Equal("In 1 collection", dialog.FooterText);
        Assert.Equal("contains_item=series%3Adark", _harness.Queries(ListPath).Single());
    }

    [Fact]
    public async Task Ticking_AddsAtTheFront_AndUnticking_Removes()
    {
        var dialog = await OpenAsync();

        await dialog.ToggleCommand.ExecuteAsync(dialog.Shown[1]);
        await dialog.ToggleCommand.ExecuteAsync(dialog.Shown[0]);

        Assert.Equal("""{"position":0}""", Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, "api/v2/collections/c2/items/series%3Adark")).Body);
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Delete, "api/v2/collections/c1/items/series%3Adark"));
        Assert.Equal([false, true], dialog.Shown.Select(r => r.IsIn));
        Assert.Equal("Added", dialog.Shown[1].Flash);
    }

    [Fact]
    public async Task ATickThatFails_GoesBack_AndSaysSo()
    {
        _harness.Silo.On(HttpMethod.Put, "api/v2/collections/c2/items/series%3Adark", ScriptedSilo.Problem(503, "unavailable"));
        var dialog = await OpenAsync();

        await dialog.ToggleCommand.ExecuteAsync(dialog.Shown[1]);

        Assert.False(dialog.Shown[1].IsIn);
        Assert.Equal("Failed to add to collection", dialog.Notice);
        Assert.Equal("In 1 collection", dialog.FooterText);
    }

    [Fact]
    public async Task TheSearch_KeepsMatchingNames()
    {
        var dialog = await OpenAsync();

        dialog.Search = "rain";
        Assert.Equal(["Rainy days"], dialog.Shown.Select(r => r.Name));

        dialog.Search = "zz";
        Assert.True(dialog.HasNoMatch);
        Assert.Equal("No collections match \u201Czz\u201D.", dialog.NoMatchText);
    }

    [Fact]
    public async Task ANewCollection_IsMadeWithTheTitleInIt()
    {
        _harness.Silo.On(HttpMethod.Post, ListPath, ScriptedSilo.Json(HttpStatusCode.Created, """
            { "id": "c9", "name": "Mind benders", "collection_type": "manual", "creator_profile_id": "7", "item_count": 0 }
            """));
        _harness.Silo.On(HttpMethod.Put, "api/v2/collections/c9/items/series%3Adark", ScriptedSilo.NoContent());
        var dialog = await OpenAsync();

        dialog.NewName = "Mind benders";
        await dialog.CreateCommand.ExecuteAsync(null);

        var body = JsonDocument.Parse(Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, ListPath)).Body!).RootElement;
        Assert.Equal("Mind benders", body.GetProperty("name").GetString());
        Assert.Equal("manual", body.GetProperty("collection_type").GetString());
        Assert.True(dialog.Shown[^1].IsIn);
        Assert.Equal("In 2 collections", dialog.FooterText);
        Assert.Equal("", dialog.NewName);
    }

    [Fact]
    public async Task WithoutCollections_TheFirstIsOffered()
    {
        _harness.Answer(ListPath, """{ "items": [] }""");

        var dialog = await OpenAsync();

        Assert.True(dialog.HasNoCollections);
        Assert.Equal("Name it and Dark goes straight in. You can add more titles and change the order later.", dialog.FirstCollectionText);
    }
}
