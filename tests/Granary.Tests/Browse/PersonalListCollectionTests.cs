using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>A collection's page: Edit and Delete for the profile's own, nothing for anyone else's.</summary>
public sealed class PersonalListCollectionTests
{
    private const string CollectionPath = "api/v2/collections/u1";

    private const string Own = """
        { "id": "u1", "name": "Rainy days", "description": "", "collection_type": "manual", "creator_profile_id": "7", "is_shared": false, "item_count": 2 }
        """;

    private static readonly CollectionRouteParameter Rainy = new(CatalogSources.UserCollection, "u1", "Rainy days");

    private readonly LibraryHarness _harness = new();
    private readonly FakeConfirmation _confirmation = new();

    private static Func<Task<HttpResponseMessage>> Versioned(string json, string etag) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return Task.FromResult(response);
    };

    private async Task<PersonalListViewModel> ShownAsync(CollectionRouteParameter collection)
    {
        _harness.Answer(WatchlistTitlesViewModelTests.StatusPath, """{ "requests_enabled": false }""");
        var list = WatchlistTitlesViewModelTests.CreateList(_harness, new ViewerStateChanges(new PlaybackStops()), _confirmation);
        await list.ShowCollectionAsync(collection);
        return list;
    }

    [Fact]
    public async Task AnOwnCollection_CanBeEdited()
    {
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Own, "\"r1\""));

        var list = await ShownAsync(Rainy);
        list.EditCollectionCommand.Execute(null);

        Assert.True(list.IsOwnCollection);
        Assert.True(list.CanEditCollection);
        Assert.Equal((AppRoute.CollectionEditor, (object?)new CollectionEditorRouteParameter("u1")), _harness.Navigation.Navigations[^1]);
    }

    [Fact]
    public async Task AnotherProfilesCollection_HasNoActions()
    {
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Own.Replace("\"creator_profile_id\": \"7\"", "\"creator_profile_id\": \"8\"", StringComparison.Ordinal), "\"r1\""));

        var list = await ShownAsync(Rainy);

        Assert.False(list.IsOwnCollection);
        Assert.False(list.DeleteCollectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task AServerCollection_IsNotAsked()
    {
        var list = await ShownAsync(new CollectionRouteParameter(CatalogSources.LibraryCollection, "s1", "Oscar Winners", "1"));

        Assert.False(list.IsOwnCollection);
        Assert.DoesNotContain(_harness.Silo.Sent, r => r.Uri.AbsolutePath.Contains("/api/v2/collections/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_AsksFirst_ThenGoesToCollections()
    {
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Own, "\"r1\""));
        _harness.Silo.On(HttpMethod.Delete, CollectionPath, ScriptedSilo.NoContent());
        var list = await ShownAsync(Rainy);

        await list.DeleteCollectionCommand.ExecuteAsync(null);

        Assert.Equal(("Delete \u201CRainy days\u201D?", "This can't be undone.", "Delete"), Assert.Single(_confirmation.Asked));
        Assert.Equal("\"r1\"", Assert.Single(_harness.Silo.SentTo(HttpMethod.Delete, CollectionPath)).IfMatch);
        Assert.Equal([AppRoute.Collections], _harness.Navigation.Sections);
    }

    [Fact]
    public async Task ADeleteThatIsCancelled_SendsNothing()
    {
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Own, "\"r1\""));
        _confirmation.Answer = false;
        var list = await ShownAsync(Rainy);

        await list.DeleteCollectionCommand.ExecuteAsync(null);

        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Delete, CollectionPath));
    }
}
