using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Tests.Profiles;

namespace Granary.Tests.Catalog;

/// <summary>A profile's own collections made and changed: paths, bodies and the versions writes send back.</summary>
public sealed class CollectionsApiTests
{
    private const string Collection = """
        { "id": "c1", "name": "Ghibli Night", "description": "Wet afternoons", "collection_type": "manual", "creator_profile_id": "7",
          "is_shared": true, "include_in_server_collections": false, "item_count": 2,
          "display_query_definition": { "match": "all", "groups": [ { "match": "all", "rules": [
            { "field": "watched", "op": "is", "value": false }, { "field": "type", "op": "is", "value": "movie" } ] } ] } }
        """;

    private static readonly Uri Server = ScriptedSilo.Base;

    private readonly ScriptedSilo _silo = new();

    private CollectionsApi Api => new(_silo.Http);

    private static Func<Task<HttpResponseMessage>> Versioned(HttpStatusCode status, string json, string etag) => () =>
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return Task.FromResult(response);
    };

    private JsonElement Body() => JsonDocument.Parse(Assert.Single(_silo.Sent).Body!).RootElement;

    [Fact]
    public async Task Get_AnswersTheCollection_AndItsVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Get, "api/v2/collections/c1", Versioned(HttpStatusCode.OK, Collection, "\"rev-4\""));

        var read = await Api.GetAsync(Server, "c1", ct);

        Assert.Equal("\"rev-4\"", read.Version);
        Assert.Equal("Wet afternoons", read.Value.Description);
        Assert.True(read.Value.IsManual);
        Assert.Equal(new CollectionShowOnly(CollectionShowOnly.Unwatched, CollectionShowOnly.Movies), CollectionShowOnly.Read(read.Value.DisplayQueryDefinition));
    }

    [Fact]
    public async Task AReadWithoutAVersion_CannotBeChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Get, "api/v2/collections/c1", ScriptedSilo.Json(HttpStatusCode.OK, Collection));

        await Assert.ThrowsAsync<HttpRequestException>(() => Api.GetAsync(Server, "c1", ct));
    }

    [Fact]
    public async Task Update_SendsTheVersionItWasReadAt_AndEveryField()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Patch, "api/v2/collections/c1", Versioned(HttpStatusCode.OK, Collection, "\"rev-5\""));

        var saved = await Api.UpdateAsync(Server, "c1", "\"rev-4\"", new PersonalCollectionUpdate
        {
            Name = "Ghibli",
            Description = "",
            IsShared = false,
            DisplayQueryDefinition = CollectionShowOnly.Everything.ToQuery(),
        }, ct);

        Assert.Equal("\"rev-5\"", saved.Version);
        Assert.Equal("\"rev-4\"", Assert.Single(_silo.Sent).IfMatch);
        Assert.Equal("""{"name":"Ghibli","description":"","is_shared":false,"include_in_server_collections":false,"display_query_definition":{"match":"all","groups":[]}}""",
            Body().GetRawText());
    }

    [Fact]
    public async Task AChangeToAnOlderVersion_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Patch, "api/v2/collections/c1", ScriptedSilo.Problem(412, "precondition_failed"));

        var refused = await Assert.ThrowsAsync<SiloApiException>(() => Api.UpdateAsync(Server, "c1", "\"rev-3\"", new PersonalCollectionUpdate
        {
            Name = "Ghibli",
            Description = "",
            DisplayQueryDefinition = CollectionShowOnly.Everything.ToQuery(),
        }, ct));

        Assert.Equal(412, refused.Status);
    }

    [Fact]
    public async Task Create_SendsTheDescription_AndShowOnlyOnlyWhenItFilters()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Post, "api/v2/collections", ScriptedSilo.Json(HttpStatusCode.Created, Collection));

        var made = await Api.CreateAsync(Server, new PersonalCollectionCreate { Name = "Ghibli Night" }, ct);

        Assert.Equal("c1", made.Id);
        Assert.Equal("""{"name":"Ghibli Night","description":"","is_shared":false,"include_in_server_collections":false,"collection_type":"manual"}""",
            Body().GetRawText());
    }

    [Fact]
    public void ShowOnly_WritesOneGroupOfRules()
    {
        var query = new CollectionShowOnly(CollectionShowOnly.Watched, CollectionShowOnly.Shows).ToQuery();

        string json = JsonSerializer.Serialize(query, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        Assert.Equal("""{"match":"all","groups":[{"match":"all","rules":[{"field":"watched","op":"is","value":true},{"field":"type","op":"is","value":"series"}]}]}""",
            json);
        Assert.Equal(new CollectionShowOnly(CollectionShowOnly.Watched, CollectionShowOnly.Shows),
            CollectionShowOnly.Read(JsonDocument.Parse(json).RootElement));
        Assert.Equal(CollectionShowOnly.Everything, CollectionShowOnly.Read(default));
    }

    [Fact]
    public async Task Delete_SendsTheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Delete, "api/v2/collections/c1", ScriptedSilo.NoContent());

        await Api.DeleteAsync(Server, "c1", "\"rev-4\"", ct);

        Assert.Equal("\"rev-4\"", Assert.Single(_silo.Sent).IfMatch);
    }

    [Fact]
    public async Task Titles_AreListed_Added_AndRemovedByTheirIds()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Get, "api/v2/collections/c1/items", ScriptedSilo.Json(HttpStatusCode.OK, """
            { "items": [ { "media_item_id": "movie:totoro", "position": 0, "title": "My Neighbor Totoro", "collection_id": "c1" } ],
              "page": { "has_more": true, "next_cursor": "p2" } }
            """));
        _silo.On(HttpMethod.Put, "api/v2/collections/c1/items/movie%3Aspirited", ScriptedSilo.NoContent());
        _silo.On(HttpMethod.Delete, "api/v2/collections/c1/items/movie%3Atotoro", ScriptedSilo.NoContent());

        var page = await Api.ListItemsAsync(Server, "c1", "p1", ct);
        await Api.AddItemAsync(Server, "c1", "movie:spirited", 3, ct);
        await Api.RemoveItemAsync(Server, "c1", "movie:totoro", ct);

        Assert.Equal("My Neighbor Totoro", Assert.Single(page.Items).Title);
        Assert.Equal("/silo/api/v2/collections/c1/items?limit=200&cursor=p1", _silo.Sent[0].Uri.PathAndQuery);
        Assert.Equal("""{"position":3}""", _silo.Sent[1].Body);
        Assert.Equal(HttpMethod.Delete, _silo.Sent[2].Method);
    }

    [Fact]
    public async Task TheOrder_HasItsOwnVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Get, "api/v2/collections/c1/items/order",
            Versioned(HttpStatusCode.OK, """{ "ordered_ids": ["movie:a", "movie:b"], "has_more": false }""", "\"order-1\""));
        _silo.On(HttpMethod.Put, "api/v2/collections/c1/items/order",
            Versioned(HttpStatusCode.OK, """{ "ordered_ids": ["movie:b", "movie:a"], "has_more": false }""", "\"order-2\""));

        var order = await Api.GetOrderAsync(Server, "c1", ct);
        var changed = await Api.SetOrderAsync(Server, "c1", order.Version, ["movie:b", "movie:a"], ct);

        Assert.Equal(["movie:a", "movie:b"], order.Value.OrderedIds);
        Assert.Equal("\"order-1\"", _silo.Sent[1].IfMatch);
        Assert.Equal("""{"ordered_ids":["movie:b","movie:a"]}""", _silo.Sent[1].Body);
        Assert.Equal("\"order-2\"", changed.Version);
    }

    [Fact]
    public async Task TheList_SaysWhichCollectionsHoldATitle()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Get, "api/v2/collections", ScriptedSilo.Json(HttpStatusCode.OK, """
            { "items": [ { "id": "c1", "name": "Ghibli Night", "collection_type": "manual", "contains": true } ] }
            """));

        var list = await Api.ListContainingAsync(Server, "movie:totoro", ct);

        Assert.True(Assert.Single(list.Items).Contains);
        Assert.Equal("/silo/api/v2/collections?contains_item=movie%3Atotoro", Assert.Single(_silo.Sent).Uri.PathAndQuery);
    }

    [Fact]
    public async Task Capabilities_TakeArtworkUnlessTheServerSaysNo()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Get, "api/v2/collections/capabilities", ScriptedSilo.Json(HttpStatusCode.OK, """{ "item_reorder": true }"""));

        var capabilities = await Api.GetCapabilitiesAsync(Server, ct);

        Assert.True(capabilities.TakesArtwork);
        Assert.True(capabilities.ItemReorder);
        Assert.False(capabilities.PosterCollages);
    }
    [Fact]
    public async Task APosterFile_IsPutAsAForm()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Put, "api/v2/collections/c1/poster", ScriptedSilo.Json(HttpStatusCode.OK, Collection));

        await Api.SetPosterAsync(Server, "c1", new byte[] { 1, 2, 3 }, "rainy.png", "image/png", ct);

        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Contains("name=poster", sent.Body, StringComparison.Ordinal);
        Assert.Contains("filename=rainy.png", sent.Body, StringComparison.Ordinal);
        Assert.Contains("Content-Type: image/png", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APosterLink_IsPutAsASourceUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Put, "api/v2/collections/c1/poster", ScriptedSilo.Json(HttpStatusCode.OK, Collection));

        await Api.SetPosterFromLinkAsync(Server, "c1", "https://images.example/rainy.jpg", ct);

        string body = Assert.Single(_silo.Sent).Body!;
        Assert.Contains("name=source_url", body, StringComparison.Ordinal);
        Assert.Contains("https://images.example/rainy.jpg", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingThePoster_DeletesThePosterImage()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Delete, "api/v2/collections/c1/image", ScriptedSilo.NoContent());

        await Api.RemovePosterAsync(Server, "c1", ct);

        Assert.Equal("/silo/api/v2/collections/c1/image?type=poster", Assert.Single(_silo.Sent).Uri.PathAndQuery);
    }

    [Fact]
    public async Task ACreate_SendsAPosterLinkOnlyWhenThereIsOne()
    {
        var ct = TestContext.Current.CancellationToken;
        _silo.On(HttpMethod.Post, "api/v2/collections", ScriptedSilo.Json(HttpStatusCode.Created, Collection));

        await Api.CreateAsync(Server, new PersonalCollectionCreate { Name = "Rainy", PosterSourceUrl = "https://images.example/rainy.jpg" }, ct);

        Assert.Equal("https://images.example/rainy.jpg", Body().GetProperty("poster_source_url").GetString());
    }
}
