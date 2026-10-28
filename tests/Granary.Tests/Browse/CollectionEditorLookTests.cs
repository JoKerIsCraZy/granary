using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.Profiles;
using Granary.Core.ViewModels.Collections;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>The collection editor's Look card: the poster it shows, and a file, a link or the collage staged until Save.</summary>
public sealed class CollectionEditorLookTests
{
    private const string CollectionPath = "api/v2/collections/c1";
    private const string ListPath = "api/v2/collections";
    private const string PosterPath = "api/v2/collections/c1/poster";
    private const string ImagePath = "api/v2/collections/c1/image";

    private const string Saved = """
        { "id": "c1", "name": "Ghibli Night", "description": "", "collection_type": "manual", "creator_profile_id": "7",
          "is_shared": false, "include_in_server_collections": false, "item_count": 2 }
        """;

    private readonly LibraryHarness _harness = new();

    public CollectionEditorLookTests()
    {
        _harness.Silo.On(HttpMethod.Get, CollectionPath, Versioned(Saved, "\"rev-1\""));
        _harness.Silo.On(HttpMethod.Patch, CollectionPath, Versioned(Saved, "\"rev-2\""));
        _harness.Answer("api/v2/collections/c1/items", """{ "items": [], "page": { "has_more": false } }""");
        _harness.Answer("api/v2/collections/capabilities", """{ "artwork": true, "poster_collages": true }""");
        _harness.Answer("api/v2/profiles", """{ "items": [ { "id": "7", "name": "Elena", "is_primary": true } ], "avatar_upload_enabled": false }""");
        Listed(poster: "", collage: false);
    }

    private void Listed(string poster, bool collage, int items = 2) => _harness.Answer(ListPath, $$"""
        { "items": [ { "id": "c1", "name": "Ghibli Night", "collection_type": "manual", "creator_profile_id": "7", "item_count": {{items}},
                       "poster_url": "{{poster}}", "poster_is_collage": {{(collage ? "true" : "false")}} } ] }
        """);

    private static Func<Task<HttpResponseMessage>> Versioned(string json, string etag) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return Task.FromResult(response);
    };

    private CollectionEditorViewModel Create() => new(_harness.Session, new CollectionsApi(_harness.Silo.Http), new CatalogApi(_harness.Silo.Http),
        new ImageParameterCache(new ImagesApi(_harness.Silo.Http), _harness.Logs.For<ImageParameterCache>()), new ProfilesApi(_harness.Silo.Http),
        _harness.Libraries(), _harness.Navigation, _harness.Clock, _harness.Logs.For<CollectionEditorViewModel>());

    private async Task<CollectionEditorViewModel> EditAsync()
    {
        var editor = Create();
        await editor.ActivateAsync("c1");
        return editor;
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    [Fact]
    public async Task ASavedImage_ShowsFromTheList()
    {
        Listed(poster: "/api/v2/artwork/user-collections/c1/poster.webp?sig=s", collage: false);

        var editor = await EditAsync();

        Assert.True(editor.ShowsLook);
        Assert.Equal("Poster: an image", editor.LookSummary);
        Assert.False(editor.HasPosterCaption);
        Assert.Equal(new Uri("https://nas.example/silo/api/v2/artwork/user-collections/c1/poster.webp?sig=s"), editor.PosterPreviewUrl);
        Assert.True(editor.CanClearPoster);
        Assert.Equal("Use the collage", editor.ClearPosterLabel);
    }

    [Fact]
    public async Task TheCollage_StandsInForAPosterOfItsOwn()
    {
        Listed(poster: "/api/v2/artwork/user-collections/c1/collage.webp?sig=c", collage: true);

        var editor = await EditAsync();

        Assert.Equal("Poster: a collage of its titles", editor.LookSummary);
        Assert.Equal("Collage of its titles", editor.PosterCaption);
        Assert.False(editor.CanClearPoster);
    }

    [Fact]
    public async Task WithoutCollages_NoPosterIsNone()
    {
        _harness.Answer("api/v2/collections/capabilities", """{ "artwork": true, "poster_collages": false }""");

        var editor = await EditAsync();

        Assert.Equal("Poster: none", editor.LookSummary);
        Assert.Equal("No poster", editor.PosterCaption);
        Assert.Equal("Remove poster", editor.ClearPosterLabel);
    }

    [Fact]
    public async Task AServerWithoutArtwork_HasNoLookCard()
    {
        _harness.Answer("api/v2/collections/capabilities", """{ "artwork": false }""");

        var editor = await EditAsync();

        Assert.False(editor.ShowsLook);
        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Get, ListPath));
    }

    [Fact]
    public async Task AChosenFile_IsStaged_ThenPutAfterThePatch()
    {
        _harness.Silo.On(HttpMethod.Put, PosterPath, ScriptedSilo.Json(HttpStatusCode.OK, Saved));
        var editor = await EditAsync();

        Assert.True(editor.UsePosterFile(Png, "ghibli.png", "image/png"));
        Assert.Equal("Poster: a new image", editor.LookSummary);
        Assert.Equal(["Poster"], editor.ChangedFields);
        Assert.Equal("Poster not saved", editor.SaveBarMessage);
        Assert.Equal("The new poster saves when you press Save.", editor.StagedPosterText);
        Assert.Same(Png, editor.PosterPreviewImage);

        Listed(poster: "/api/v2/artwork/user-collections/c1/poster.webp?sig=n", collage: false);
        await editor.SaveCommand.ExecuteAsync(null);

        var order = _harness.Silo.Sent.Where(r => r.Method == HttpMethod.Patch || r.Method == HttpMethod.Put).Select(r => r.Method).ToList();
        Assert.Equal([HttpMethod.Patch, HttpMethod.Put], order);
        Assert.Contains("filename=ghibli.png", Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, PosterPath)).Body, StringComparison.Ordinal);
        Assert.False(editor.IsPosterStaged);
        Assert.Empty(editor.ChangedFields);
        Assert.Equal("Poster: an image", editor.LookSummary);
    }

    [Fact]
    public async Task ALink_IsPutAsTheSourceUrl()
    {
        _harness.Silo.On(HttpMethod.Put, PosterPath, ScriptedSilo.Json(HttpStatusCode.OK, Saved));
        var editor = await EditAsync();

        editor.PastePosterLinkCommand.Execute(null);
        editor.PosterLink = "  https://images.example/totoro.jpg ";
        editor.UsePosterLinkCommand.Execute(null);
        Assert.False(editor.IsPastingLink);
        Assert.Equal(new Uri("https://images.example/totoro.jpg"), editor.PosterPreviewUrl);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Contains("https://images.example/totoro.jpg", Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, PosterPath)).Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ftp://images.example/a.jpg")]
    [InlineData("images.example/a.jpg")]
    [InlineData("file:///C:/a.jpg")]
    public async Task ALinkThatIsNoWebAddress_IsNotTaken(string link)
    {
        var editor = await EditAsync();
        editor.PastePosterLinkCommand.Execute(null);
        editor.PosterLink = link;

        editor.UsePosterLinkCommand.Execute(null);

        Assert.False(editor.IsPosterStaged);
        Assert.True(editor.IsPastingLink);
        Assert.Equal(CollectionArtwork.NotAWebAddress, editor.PosterPickError);
    }

    [Fact]
    public async Task AFileTheServerWouldRefuse_IsNotTaken()
    {
        var editor = await EditAsync();

        Assert.False(editor.UsePosterFile(Png, "ghibli.gif", "image/gif"));
        Assert.Equal("Choose a JPEG, PNG or WebP image.", editor.PosterPickError);
        Assert.False(editor.UsePosterFile(new byte[CollectionsApi.PosterMaxBytes + 1], "big.jpg", "image/jpeg"));
        Assert.Equal("Choose an image of 10 MB or less.", editor.PosterPickError);
        Assert.False(editor.IsPosterStaged);
    }

    [Fact]
    public async Task UseTheCollage_RemovesTheSavedImageOnSave()
    {
        Listed(poster: "/api/v2/artwork/user-collections/c1/poster.webp?sig=s", collage: false);
        _harness.Silo.On(HttpMethod.Delete, ImagePath, ScriptedSilo.NoContent());
        var editor = await EditAsync();

        editor.ClearPosterCommand.Execute(null);
        Assert.Equal("The poster is removed when you save.", editor.StagedPosterText);
        Assert.Equal("Poster: a collage once its titles have posters", editor.LookSummary);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("/silo/api/v2/collections/c1/image?type=poster", Assert.Single(_harness.Silo.SentTo(HttpMethod.Delete, ImagePath)).Uri.PathAndQuery);
    }

    [Fact]
    public async Task APosterThatFails_StaysStaged_WithRetry_AndTheCollectionIsSaved()
    {
        _harness.Silo.On(HttpMethod.Put, PosterPath, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.Json(HttpStatusCode.OK, Saved));
        var editor = await EditAsync();
        editor.UsePosterFile(Png, "ghibli.png", "image/png");

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(editor.IsPosterStaged);
        Assert.True(editor.HasPosterError);
        Assert.StartsWith("Couldn't save the poster. ", editor.PosterErrorText, StringComparison.Ordinal);
        Assert.True(editor.IsLookOpen);
        Assert.False(editor.HasSaveError);
        Assert.Equal(["Poster"], editor.ChangedFields);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(editor.IsPosterStaged);
        Assert.False(editor.HasPosterError);
    }

    [Fact]
    public async Task UndoAndDiscard_DropTheStagedPoster()
    {
        var editor = await EditAsync();
        editor.UsePosterFile(Png, "ghibli.png", "image/png");

        editor.UndoPosterCommand.Execute(null);
        Assert.False(editor.IsPosterStaged);
        Assert.Empty(editor.ChangedFields);

        editor.UsePosterFile(Png, "ghibli.png", "image/png");
        editor.DiscardCommand.Execute(null);
        Assert.False(editor.IsPosterStaged);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task ANewCollection_TakesALinkInItsCreate_AndAFileAfter()
    {
        _harness.Silo.On(HttpMethod.Post, ListPath, ScriptedSilo.Json(HttpStatusCode.Created, Saved));
        _harness.Silo.On(HttpMethod.Put, PosterPath, ScriptedSilo.Json(HttpStatusCode.OK, Saved));
        var linked = Create();
        await linked.ActivateAsync(null);
        linked.Name = "Ghibli Night";
        linked.PastePosterLinkCommand.Execute(null);
        linked.PosterLink = "https://images.example/totoro.jpg";
        linked.UsePosterLinkCommand.Execute(null);
        Assert.True(linked.HasUnsavedChanges);

        await linked.SaveCommand.ExecuteAsync(null);

        var body = JsonDocument.Parse(Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, ListPath)).Body!).RootElement;
        Assert.Equal("https://images.example/totoro.jpg", body.GetProperty("poster_source_url").GetString());
        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Put, PosterPath));
        Assert.False(linked.IsPosterStaged);

        var filed = Create();
        await filed.ActivateAsync(null);
        filed.Name = "Ghibli Night";
        filed.UsePosterFile(Png, "ghibli.png", "image/png");

        await filed.SaveCommand.ExecuteAsync(null);

        Assert.False(JsonDocument.Parse(_harness.Silo.SentTo(HttpMethod.Post, ListPath)[^1].Body!).RootElement.TryGetProperty("poster_source_url", out _));
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Put, PosterPath));
    }

    [Fact]
    public async Task AfterASave_TheCollageIsWaitedFor()
    {
        var editor = await EditAsync();
        editor.Name = "Ghibli";
        int reads = _harness.Silo.SentTo(HttpMethod.Get, ListPath).Count;

        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Poster: a collage once its titles have posters", editor.LookSummary);
        Listed(poster: "/api/v2/artwork/user-collections/c1/collage.webp?sig=c", collage: true);
        _harness.Clock.Advance(CollectionEditorViewModel.CollagePoll);
        await editor.CollageWaiting;

        Assert.Equal("Poster: a collage of its titles", editor.LookSummary);
        Assert.Equal(reads + 2, _harness.Silo.SentTo(HttpMethod.Get, ListPath).Count);
    }

    [Fact]
    public async Task TheCollageWait_EndsAfterAWhile()
    {
        var editor = await EditAsync();
        editor.Name = "Ghibli";
        await editor.SaveCommand.ExecuteAsync(null);

        for (int i = 0; i < 6; i++)
        {
            _harness.Clock.Advance(CollectionEditorViewModel.CollagePoll);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        await editor.CollageWaiting;

        Assert.Equal("Poster: a collage once its titles have posters", editor.LookSummary);
        Assert.True(_harness.Silo.SentTo(HttpMethod.Get, ListPath).Count <= 2 + 5);
    }
}
