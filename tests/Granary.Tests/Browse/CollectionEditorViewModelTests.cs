using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.Profiles;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Collections;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>A profile's own manual collection made and changed, as the web's collection editor.</summary>
public sealed class CollectionEditorViewModelTests
{
    private const string CollectionPath = "api/v2/collections/c1";
    private const string ItemsPath = "api/v2/collections/c1/items";
    private const string OrderPath = "api/v2/collections/c1/items/order";

    private const string Saved = """
        { "id": "c1", "name": "Ghibli Night", "description": "Wet afternoons", "collection_type": "manual", "creator_profile_id": "7",
          "is_shared": true, "include_in_server_collections": false, "item_count": 2,
          "display_query_definition": { "match": "all", "groups": [ { "match": "all", "rules": [ { "field": "watched", "op": "is", "value": false } ] } ] } }
        """;

    private const string Items = """
        { "items": [ { "media_item_id": "movie:alien-1979", "position": 0, "title": "Alien" }, { "media_item_id": "movie:heat-1995", "position": 1, "title": "Heat" } ],
          "page": { "has_more": false } }
        """;

    private readonly LibraryHarness _harness = new();

    public CollectionEditorViewModelTests()
    {
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Saved, "\"rev-1\""));
        _harness.Answer(ItemsPath, Items);
        _harness.Answer("api/v2/collections/capabilities", """{ "item_reorder": true }""");
        _harness.Silo.On(HttpMethod.Get, OrderPath, Versioned("""{ "ordered_ids": ["movie:alien-1979", "movie:heat-1995"], "has_more": false }""", "\"o1\""));
        _harness.Answer("api/v2/profiles", """
            { "items": [ { "id": "7", "name": "Elena", "is_primary": true, "has_pin": false, "is_child": false },
                         { "id": "8", "name": "Maya", "is_primary": false, "has_pin": false, "is_child": false } ], "avatar_upload_enabled": false }
            """);
    }

    private static Func<Task<HttpResponseMessage>> Versioned(string json, string etag, HttpStatusCode status = HttpStatusCode.OK) => () =>
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return Task.FromResult(response);
    };

    private CollectionEditorViewModel Create() => new(_harness.Session, new CollectionsApi(_harness.Silo.Http), new CatalogApi(_harness.Silo.Http),
        new ImageParameterCache(new ImagesApi(_harness.Silo.Http), _harness.Logs.For<ImageParameterCache>()), new ProfilesApi(_harness.Silo.Http),
        _harness.Navigation, _harness.Clock, _harness.Logs.For<CollectionEditorViewModel>());

    private async Task<CollectionEditorViewModel> EditAsync()
    {
        var editor = Create();
        await editor.ActivateAsync("c1");
        return editor;
    }

    private SentRequest Last(HttpMethod method, string path) => _harness.Silo.SentTo(method, path)[^1];

    private static JsonElement Json(SentRequest sent) => JsonDocument.Parse(sent.Body!).RootElement;

    [Fact]
    public async Task ACollection_ShowsItsFields_AndItsTitles()
    {
        var editor = await EditAsync();

        Assert.True(editor.IsReady);
        Assert.Equal("Ghibli Night", editor.HeaderTitle);
        Assert.Equal("Wet afternoons", editor.Description);
        Assert.Equal(CollectionShowOnly.Unwatched, editor.Watch.Value);
        Assert.True(editor.IsShared);
        Assert.True(editor.IsSavedShared);
        Assert.True(editor.CanShare);
        Assert.Equal(["Alien", "Heat"], editor.Titles.Select(t => t.Title));
        Assert.Equal("1979 · Movie", editor.Titles[0].Meta);
        Assert.Equal([1, 2], editor.Titles.Select(t => t.Number));
        Assert.Equal("2 titles", editor.MetaLine);
        Assert.True(editor.CanReorder);
        Assert.False(editor.ShowsSaveBar);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task AChangedField_ShowsTheSaveBar_AndSavesAtTheVersionItWasReadAt()
    {
        var editor = await EditAsync();
        _harness.Silo.On(HttpMethod.Patch, CollectionPath, Versioned(Saved.Replace("Ghibli Night", "Ghibli", StringComparison.Ordinal), "\"rev-2\""));

        editor.Name = "Ghibli";
        editor.Description = "";

        Assert.True(editor.ShowsSaveBar);
        Assert.Equal("Name and description not saved", editor.SaveBarMessage);
        Assert.Equal("Titles are already saved.", editor.SaveBarHint);
        Assert.True(editor.HasUnsavedChanges);

        await editor.SaveCommand.ExecuteAsync(null);

        var sent = Last(HttpMethod.Patch, CollectionPath);
        Assert.Equal("\"rev-1\"", sent.IfMatch);
        Assert.Equal("Ghibli", Json(sent).GetProperty("name").GetString());
        Assert.Equal("", Json(sent).GetProperty("description").GetString());
    }

    [Fact]
    public async Task ShowOnly_IsSavedAsItsRules()
    {
        var editor = await EditAsync();
        _harness.Silo.On(HttpMethod.Patch, CollectionPath, Versioned(Saved, "\"rev-2\""));

        editor.Media = CollectionEditorViewModel.MediaChoices[2];
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new CollectionShowOnly(CollectionShowOnly.Unwatched, CollectionShowOnly.Shows),
            CollectionShowOnly.Read(Json(Last(HttpMethod.Patch, CollectionPath)).GetProperty("display_query_definition")));
    }

    [Fact]
    public async Task AChangeMadeElsewhere_IsMergedIn_AndSavedAgain()
    {
        var editor = await EditAsync();
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Saved.Replace("Wet afternoons", "Rainy days", StringComparison.Ordinal), "\"rev-2\""));
        _harness.Silo.On(HttpMethod.Patch, CollectionPath, ScriptedSilo.Problem(412, "precondition_failed"), Versioned(Saved, "\"rev-3\""));

        editor.Name = "Ghibli";
        await editor.SaveCommand.ExecuteAsync(null);

        var retried = Last(HttpMethod.Patch, CollectionPath);
        Assert.Equal("\"rev-2\"", retried.IfMatch);
        Assert.Equal("Ghibli", Json(retried).GetProperty("name").GetString());
        Assert.Equal("Rainy days", Json(retried).GetProperty("description").GetString());
        Assert.Equal("Rainy days", editor.Description);
        Assert.False(editor.HasConflict);
    }

    [Fact]
    public async Task AFieldChangedOnBothSides_WaitsForAPick()
    {
        var editor = await EditAsync();
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Saved.Replace("Ghibli Night", "Totoro Night", StringComparison.Ordinal), "\"rev-2\""));
        _harness.Silo.On(HttpMethod.Patch, CollectionPath, ScriptedSilo.Problem(412, "precondition_failed"));

        editor.Name = "Ghibli";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["Name"], editor.Conflicts);
        Assert.Equal("Name changed in both places.", editor.ConflictText);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.UseTheirsCommand.Execute(null);

        Assert.Equal("Totoro Night", editor.Name);
        Assert.False(editor.HasConflict);
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Patch, CollectionPath));
    }

    [Fact]
    public async Task KeepMine_SavesThisSidesValue_AtTheNewVersion()
    {
        var editor = await EditAsync();
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Saved.Replace("Ghibli Night", "Totoro Night", StringComparison.Ordinal), "\"rev-2\""));
        _harness.Silo.On(HttpMethod.Patch, CollectionPath, ScriptedSilo.Problem(412, "precondition_failed"), Versioned(Saved, "\"rev-3\""));
        editor.Name = "Ghibli";
        await editor.SaveCommand.ExecuteAsync(null);

        editor.KeepMineCommand.Execute(null);
        await editor.SaveCommand.ExecuteAsync(null);

        var sent = Last(HttpMethod.Patch, CollectionPath);
        Assert.Equal("\"rev-2\"", sent.IfMatch);
        Assert.Equal("Ghibli", Json(sent).GetProperty("name").GetString());
    }

    [Fact]
    public async Task ANewCollection_IsMade_WithTheTitlesPickedForIt()
    {
        // The server answers what it made: the name, and nothing else set.
        _harness.Silo.On(HttpMethod.Post, "api/v2/collections", ScriptedSilo.Json(HttpStatusCode.Created, """
            { "id": "c1", "name": "Ghibli Night", "description": "", "collection_type": "manual", "creator_profile_id": "7", "is_shared": false }
            """));
        _harness.Silo.On(HttpMethod.Put, "api/v2/collections/c1/items/movie%3Aheat-1995", ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Put, "api/v2/collections/c1/items/movie%3Aalien-1979", ScriptedSilo.NoContent());
        var editor = Create();
        await editor.ActivateAsync(null);

        Assert.True(editor.IsNew);
        Assert.Equal("New collection", editor.HeaderTitle);
        Assert.Equal("Name it, then create it.", editor.SaveBarHint);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.Name = "Ghibli Night";
        await editor.AddTitleCommand.ExecuteAsync(new CollectionSearchResult("movie:heat-1995", "Heat", "1995 · Movie", null, false, editor.AddTitleCommand));
        await editor.AddTitleCommand.ExecuteAsync(new CollectionSearchResult("movie:alien-1979", "Alien", "1979 · Movie", null, false, editor.AddTitleCommand));
        Assert.Equal("2 titles ready to add", editor.SaveBarHint);
        Assert.Equal("Create collection", editor.SaveLabel);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Ghibli Night", Json(Last(HttpMethod.Post, "api/v2/collections")).GetProperty("name").GetString());
        Assert.Equal("""{"position":0}""", Last(HttpMethod.Put, "api/v2/collections/c1/items/movie%3Aheat-1995").Body);
        Assert.Equal("""{"position":1}""", Last(HttpMethod.Put, "api/v2/collections/c1/items/movie%3Aalien-1979").Body);
        Assert.Equal("c1", editor.CollectionId);
        Assert.False(editor.IsNew);
        Assert.False(editor.HasUnsavedChanges);
        Assert.Equal(["Alien", "Heat"], editor.Titles.Select(t => t.Title));
    }

    [Fact]
    public async Task ARemovedTitle_CanBePutBack_ForAFewSeconds()
    {
        _harness.Silo.On(HttpMethod.Delete, "api/v2/collections/c1/items/movie%3Aalien-1979", ScriptedSilo.NoContent());
        _harness.Silo.On(HttpMethod.Put, "api/v2/collections/c1/items/movie%3Aalien-1979", ScriptedSilo.NoContent());
        var editor = await EditAsync();

        await editor.RemoveTitleCommand.ExecuteAsync(editor.Titles[0]);
        Assert.Equal("Removed Alien", editor.RemovedText);
        Assert.Equal(["Heat"], editor.Titles.Select(t => t.Title));

        await editor.UndoRemoveCommand.ExecuteAsync(null);

        Assert.Equal("""{"position":0}""", Last(HttpMethod.Put, "api/v2/collections/c1/items/movie%3Aalien-1979").Body);
        Assert.Equal(["Alien", "Heat"], editor.Titles.Select(t => t.Title));
        Assert.False(editor.HasRemoved);

        await editor.RemoveTitleCommand.ExecuteAsync(editor.Titles[0]);
        _harness.Clock.Advance(CollectionEditorViewModel.UndoWindow);
        await WaitUntilAsync(() => !editor.HasRemoved);
    }

    [Fact]
    public async Task AnAddedTitle_GoesAtTheEnd()
    {
        _harness.Silo.On(HttpMethod.Put, "api/v2/collections/c1/items/movie%3Atotoro", ScriptedSilo.NoContent());
        var editor = await EditAsync();

        await editor.AddTitleCommand.ExecuteAsync(new CollectionSearchResult("movie:totoro", "My Neighbor Totoro", "1988 · Movie", null, false, editor.AddTitleCommand));

        Assert.Equal("""{"position":2}""", Last(HttpMethod.Put, "api/v2/collections/c1/items/movie%3Atotoro").Body);
        Assert.Equal("My Neighbor Totoro", editor.Titles[^1].Title);
        Assert.Equal("Saved", editor.TitlesStatus);
    }

    [Fact]
    public async Task ANewOrder_IsSavedAtTheOrdersVersion()
    {
        _harness.Silo.On(HttpMethod.Put, OrderPath, Versioned("""{ "ordered_ids": ["movie:heat-1995", "movie:alien-1979"], "has_more": false }""", "\"o2\""));
        var editor = await EditAsync();

        await editor.ApplyShownOrderAsync(["movie:heat-1995", "movie:alien-1979"]);

        var sent = Last(HttpMethod.Put, OrderPath);
        Assert.Equal("\"o1\"", sent.IfMatch);
        Assert.Equal("""{"ordered_ids":["movie:heat-1995","movie:alien-1979"]}""", sent.Body);
        Assert.Equal(["Heat", "Alien"], editor.Titles.Select(t => t.Title));
        Assert.Equal([1, 2], editor.Titles.Select(t => t.Number));
    }

    [Fact]
    public async Task WithoutReorderingOnTheServer_TheOrderStays()
    {
        _harness.Answer("api/v2/collections/capabilities", """{ "item_reorder": false }""");
        var editor = await EditAsync();

        await editor.ApplyShownOrderAsync(["movie:heat-1995", "movie:alien-1979"]);

        Assert.False(editor.CanReorder);
        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Put, OrderPath));
    }

    [Fact]
    public async Task TheSearch_WaitsForAPause_AndMarksTheTitlesAlreadyIn()
    {
        var editor = await EditAsync();
        int Searches() => _harness.Queries(LibraryHarness.CatalogPath).Count(q => q.Contains("q=al", StringComparison.Ordinal));

        editor.TitleQuery = "al";
        _harness.Clock.Advance(CollectionEditorViewModel.SearchDelay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, Searches());
        _harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await editor.Searching;

        Assert.Equal(1, Searches());
        Assert.Contains("limit=12", _harness.Queries(LibraryHarness.CatalogPath).Last(q => q.Contains("q=al", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal(["Alien", "Heat"], editor.Results.Select(r => r.Title));
        Assert.All(editor.Results, r => Assert.Equal("In this collection", r.ActionText));
    }

    [Fact]
    public async Task Delete_SendsTheVersion_AndGoesToCollections()
    {
        _harness.Silo.On(HttpMethod.Delete, CollectionPath, ScriptedSilo.NoContent());
        var editor = await EditAsync();
        editor.Name = "Unsaved";

        await editor.DeleteAsync();

        Assert.Equal("\"rev-1\"", Last(HttpMethod.Delete, CollectionPath).IfMatch);
        Assert.Equal([AppRoute.Collections], _harness.Navigation.Sections);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task AnotherProfilesCollection_IsNotChangedHere()
    {
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Saved.Replace("\"creator_profile_id\": \"7\"", "\"creator_profile_id\": \"8\"", StringComparison.Ordinal), "\"rev-1\""));

        var editor = await EditAsync();

        Assert.True(editor.HasLoadError);
        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Get, ItemsPath));
    }

    [Fact]
    public async Task TurningSharingOff_SaysWhoLosesIt()
    {
        var editor = await EditAsync();

        editor.IsShared = false;

        Assert.True(editor.ShowsUnshareWarning);
        Assert.Equal("When you save, Maya loses it, including rows they made from it.", editor.UnshareWarning);
        Assert.Equal("Show to other profiles not saved", editor.SaveBarMessage);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
}
