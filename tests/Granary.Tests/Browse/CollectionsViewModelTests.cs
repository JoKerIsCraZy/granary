using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Profiles;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>The Collections page: the profile's own, the shared and the server's collections, as Silo's web shows them.</summary>
public sealed class CollectionsViewModelTests
{
    private const string PersonalPath = "api/v2/collections";
    private const string ServerPath = "api/v2/collections/server";
    private const string ProfilesPath = "api/v2/profiles";
    private const string FilmeTabPath = "api/v2/library/1/collections";
    private const string SerienTabPath = "api/v2/library/2/collections";

    private readonly LibraryHarness _harness = new();
    private readonly FakeConfirmation _confirmation = new();

    public CollectionsViewModelTests()
    {
        _harness.Answer(PersonalPath, CollectionPayloads.Personal);
        _harness.Answer(ServerPath, CollectionPayloads.Server);
        _harness.Answer(ProfilesPath, CollectionPayloads.Profiles);
        _harness.Answer(FilmeTabPath, CollectionPayloads.FilmeTab);
        _harness.Answer(SerienTabPath, CollectionPayloads.SerienTab);
    }

    private CollectionsViewModel Create() => new(
        _harness.Session,
        new CollectionsApi(_harness.Silo.Http),
        new LibraryPageApi(_harness.Silo.Http),
        new ProfilesApi(_harness.Silo.Http),
        _harness.Navigation,
        _confirmation,
        _harness.Logs.For<CollectionsViewModel>());

    private async Task<CollectionsViewModel> LoadedAsync()
    {
        var page = Create();
        await page.ActivateAsync(isReturn: false);
        return page;
    }

    private static List<string> Titles(IEnumerable<MediaCard> cards) => [.. cards.Select(c => c.Title)];

    [Fact]
    public void NewCollection_OpensTheEditor_ForTheKindPicked()
    {
        var page = Create();

        page.NewCollectionCommand.Execute(PersonalCollectionTypes.Smart);

        Assert.Equal((AppRoute.CollectionEditor, (object?)new CollectionEditorRouteParameter(null, PersonalCollectionTypes.Smart)),
            Assert.Single(_harness.Navigation.Navigations));
    }

    [Fact]
    public async Task OwnCollections_ShowInTheProfilesOrder_WithTheirKindAndCount()
    {
        var page = await LoadedAsync();

        Assert.Equal(LibraryLoadState.Ready, page.State);
        Assert.Equal(["Rainy days", "Top Picks"], Titles(page.Own));
        Assert.Equal(["Manual · 4 titles", "Synced list · 1 title · Sync failed"], page.Own.Select(c => c.Subtitle));
        Assert.Equal(["SHARED", null], page.Own.Select(c => c.StatusBadge));
        Assert.Equal(new Uri("https://nas.example/silo/api/v2/artwork/user-collections/u1/poster.webp?sig=s"), page.Own[0].ImageUrl);
        Assert.Equal("2", page.OwnCountText);
        Assert.False(page.IsOwnEmpty);
    }

    [Fact]
    public async Task SharedCollections_GroupByOwner_InTheLoginsProfileOrder()
    {
        var page = await LoadedAsync();

        Assert.Equal(["by Mara", "by Another profile"], page.Shared.Select(s => s.Heading));
        Assert.Equal(["M", "A"], page.Shared.Select(s => s.Initial));
        Assert.Equal(["Cartoons"], Titles(page.Shared[0].Cards));
        Assert.Equal("Smart · 9 titles", page.Shared[1].Cards[0].Subtitle);
        // Shared is only said of the profile's own.
        Assert.Null(page.Shared[1].Cards[0].StatusBadge);
        Assert.Equal("2", page.SharedCountText);
        Assert.Equal("Yours, the ones other profiles share with you, and the server's.", page.Subtitle);
    }

    [Fact]
    public async Task AOneProfileLogin_HasNothingShared()
    {
        _harness.Answer(PersonalPath, CollectionPayloads.NoPersonal);
        _harness.Answer(ProfilesPath, CollectionPayloads.OneProfile);

        var page = await LoadedAsync();

        Assert.Equal("Yours and the server's.", page.Subtitle);
        Assert.True(page.IsOwnEmpty);
        Assert.False(page.HasShared);
    }

    [Fact]
    public async Task APersonalCollection_OpensAsAListOfItsTitles()
    {
        var page = await LoadedAsync();

        page.OpenCommand.Execute(page.Shared[0].Cards[0]);

        Assert.Equal((AppRoute.Collection, (object?)new CollectionRouteParameter(CatalogSources.UserCollection, "u3", "Cartoons")),
            Assert.Single(_harness.Navigation.Navigations));
    }

    [Fact]
    public async Task ServerCollections_ShowEveryLibrarysFirstFew_EachOnce()
    {
        var page = await LoadedAsync();
        var server = page.Server;

        Assert.True(server.IsShown);
        Assert.Equal(["All", "Filme", "Serien"], server.LibraryChoices.Select(c => c.Label));
        Assert.Equal(["Oscar Winners", "Bond", "Anime"], Titles(server.Cards));
        Assert.Equal(["12 titles", "25 titles", "1 title"], server.Cards.Select(c => c.Subtitle));
        Assert.True(server.CanLoadMore);
        // The rest waits for the end of the grid.
        Assert.Equal(0, _harness.Count(FilmeTabPath));
    }

    [Fact]
    public async Task LoadMore_AddsOneLibrarysRestAtATime_BehindTheFirstFew()
    {
        var page = await LoadedAsync();
        var server = page.Server;

        server.LoadMoreCommand.Execute(null);
        await WaitUntilAsync(() => !server.IsLoadingMore);
        Assert.Equal(["Oscar Winners", "Bond", "Anime", "Pixar"], Titles(server.Cards));
        Assert.True(server.CanLoadMore);
        Assert.Equal(0, _harness.Count(SerienTabPath));

        // The end of the grid in view asks for the next.
        server.ShowViewportCommand.Execute(new GridViewport(0, server.Cards.Count - 1, 3, IsAtEnd: false));
        await WaitUntilAsync(() => !server.IsLoadingMore);
        Assert.Equal(["Oscar Winners", "Bond", "Anime", "Pixar", "Sitcoms"], Titles(server.Cards));
        Assert.False(server.CanLoadMore);
    }

    [Fact]
    public async Task ViewportShort_OfTheEnd_LoadsNothing()
    {
        var page = await LoadedAsync();

        page.Server.ShowViewportCommand.Execute(new GridViewport(0, 1, 1, IsAtEnd: false));

        Assert.Equal(0, _harness.Count(FilmeTabPath));
    }

    [Fact]
    public async Task APickedLibrary_ShowsAllItsCollectionsAtOnce()
    {
        var page = await LoadedAsync();
        var server = page.Server;

        server.SelectedLibrary = server.LibraryChoices.Single(c => c.Label == "Serien");
        await WaitUntilAsync(() => !server.IsLoadingMore);

        Assert.Equal(["Bond", "Anime", "Sitcoms"], Titles(server.Cards));
        Assert.False(server.CanLoadMore);

        // Back on All, a rest it never asked for stays out.
        server.SelectedLibrary = server.LibraryChoices[0];
        Assert.Equal(["Oscar Winners", "Bond", "Anime"], Titles(server.Cards));
    }

    [Fact]
    public async Task AServerCollection_OpensInItsLibrary_OrAcrossAllThatHaveIt()
    {
        var page = await LoadedAsync();
        var server = page.Server;

        server.OpenCommand.Execute(server.Cards[0]);
        server.OpenCommand.Execute(server.Cards[1]);
        server.SelectedLibrary = server.LibraryChoices.Single(c => c.Label == "Serien");
        server.OpenCommand.Execute(server.Cards.Single(c => c.Title == "Bond"));

        Assert.Equal(
        [
            (AppRoute.Collection, new CollectionRouteParameter(CatalogSources.LibraryCollection, "s1", "Oscar Winners", "1")),
            (AppRoute.Collection, new CollectionRouteParameter(CatalogSources.LibraryCollection, "s2", "Bond")),
            (AppRoute.Collection, new CollectionRouteParameter(CatalogSources.LibraryCollection, "s2", "Bond", "2")),
        ], _harness.Navigation.Navigations.Select(n => (n.Route, n.Parameter)).ToList<(AppRoute, object?)>());
    }

    [Fact]
    public async Task ALoneLibrary_HasNoPills()
    {
        _harness.Answer(ServerPath, CollectionPayloads.ServerOneLibrary);

        var page = await LoadedAsync();

        Assert.Empty(page.Server.LibraryChoices);
        Assert.Equal(["Oscar Winners"], Titles(page.Server.Cards));
        Assert.False(page.Server.CanLoadMore);
    }

    [Fact]
    public async Task ARestThatFails_SaysSo_AndCanBeTriedAgain()
    {
        _harness.Answer(FilmeTabPath, ScriptedSilo.Problem(500, "internal"), ScriptedSilo.Json(System.Net.HttpStatusCode.OK, CollectionPayloads.FilmeTab));
        var page = await LoadedAsync();
        var server = page.Server;

        server.LoadMoreCommand.Execute(null);
        await WaitUntilAsync(() => !server.IsLoadingMore);
        Assert.Equal("Couldn't load every collection in Filme.", server.FailureText);
        Assert.Equal(["Oscar Winners", "Bond", "Anime"], Titles(server.Cards));

        server.RetryCommand.Execute(null);
        await WaitUntilAsync(() => !server.IsLoadingMore);
        Assert.False(server.HasFailure);
        Assert.Contains("Pixar", Titles(server.Cards));
    }

    [Fact]
    public async Task ARestRetriedAfterALaterLibrarys_TakesItsPlace_WithoutResettingTheGrid()
    {
        _harness.Answer(FilmeTabPath, ScriptedSilo.Problem(500, "internal"), ScriptedSilo.Json(System.Net.HttpStatusCode.OK, CollectionPayloads.FilmeTab));
        var page = await LoadedAsync();
        var server = page.Server;
        server.LoadMoreCommand.Execute(null);
        await WaitUntilAsync(() => !server.IsLoadingMore);
        server.LoadMoreCommand.Execute(null);
        await WaitUntilAsync(() => !server.IsLoadingMore);
        Assert.Equal(["Oscar Winners", "Bond", "Anime", "Sitcoms"], Titles(server.Cards));
        bool reset = false;
        server.Cards.CollectionChanged += (_, e) => reset |= e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset;

        server.RetryCommand.Execute(null);
        await WaitUntilAsync(() => !server.IsLoadingMore);

        Assert.Equal(["Oscar Winners", "Bond", "Anime", "Pixar", "Sitcoms"], Titles(server.Cards));
        Assert.False(reset);
    }

    [Fact]
    public async Task ARestThatThrowsUnexpectedly_CountsAsFailed()
    {
        _harness.Answer(FilmeTabPath, () => Task.FromException<System.Net.Http.HttpResponseMessage>(new InvalidOperationException("boom")));
        var page = await LoadedAsync();
        var server = page.Server;

        server.LoadMoreCommand.Execute(null);
        await WaitUntilAsync(() => !server.IsLoadingMore);

        Assert.Equal("Couldn't load every collection in Filme.", server.FailureText);
        Assert.True(server.CanLoadMore);
    }

    [Fact]
    public async Task ARestStillOnItsWay_WhenThePageLoadsAgain_IsDropped()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(FilmeTabPath, LibraryHarness.Held(release, CollectionPayloads.FilmeTab), ScriptedSilo.Json(System.Net.HttpStatusCode.OK, CollectionPayloads.FilmeTab));
        var page = await LoadedAsync();
        page.Server.LoadMoreCommand.Execute(null);
        Assert.True(page.Server.IsLoadingMore);

        await page.LoadCommand.ExecuteAsync(null);
        release.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["Oscar Winners", "Bond", "Anime"], Titles(page.Server.Cards));
        Assert.False(page.Server.IsLoadingMore);
        Assert.True(page.Server.CanLoadMore);
    }

    [Fact]
    public async Task AnEarlierLoad_AnsweringLate_LeavesTheNewerOneShown()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(PersonalPath, LibraryHarness.Held(release, CollectionPayloads.NoPersonal), ScriptedSilo.Json(System.Net.HttpStatusCode.OK, CollectionPayloads.Personal));
        var page = Create();
        var first = page.ActivateAsync(isReturn: false);

        await page.LoadCommand.ExecuteAsync(null);
        release.SetResult();
        await first;

        Assert.Equal(LibraryLoadState.Ready, page.State);
        Assert.Equal(["Rainy days", "Top Picks"], Titles(page.Own));
        Assert.Equal(3, page.Server.Cards.Count);
    }

    [Fact]
    public async Task PersonalCollectionsThatFail_LeaveTheServersShown()
    {
        _harness.Answer(PersonalPath, ScriptedSilo.Problem(503, "unavailable"));

        var page = await LoadedAsync();

        Assert.Equal(LibraryLoadState.Ready, page.State);
        Assert.Equal(LibraryErrors.Busy, page.OwnErrorMessage);
        Assert.False(page.IsOwnEmpty);
        Assert.Equal(3, page.Server.Cards.Count);
    }

    [Fact]
    public async Task ServerCollectionsThatFail_LeaveTheProfilesShown()
    {
        _harness.Answer(ServerPath, ScriptedSilo.NetworkError());

        var page = await LoadedAsync();

        Assert.Equal(LibraryLoadState.Ready, page.State);
        Assert.Equal(LibraryErrors.Unreachable, page.ServerErrorMessage);
        Assert.False(page.Server.IsShown);
        Assert.Equal(2, page.Own.Count);
    }

    [Fact]
    public async Task BothListsFailing_FailThePage_UntilRetried()
    {
        _harness.Answer(PersonalPath, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.Json(System.Net.HttpStatusCode.OK, CollectionPayloads.Personal));
        _harness.Answer(ServerPath, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.Json(System.Net.HttpStatusCode.OK, CollectionPayloads.Server));

        var page = await LoadedAsync();
        Assert.Equal(LibraryLoadState.Error, page.State);
        Assert.Equal(LibraryErrors.Busy, page.ErrorMessage);

        await page.LoadCommand.ExecuteAsync(null);
        Assert.Equal(LibraryLoadState.Ready, page.State);
        Assert.Equal(2, page.Own.Count);
    }

    [Fact]
    public async Task BackFromACollection_KeepsThePage_WhileTheSidebarLoadsItAgain()
    {
        var page = await LoadedAsync();

        await page.ActivateAsync(isReturn: true);
        Assert.Equal(1, _harness.Count(PersonalPath));

        await page.ActivateAsync(isReturn: false);
        Assert.Equal(2, _harness.Count(PersonalPath));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
    private static Func<Task<HttpResponseMessage>> Versioned(string json, string etag) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return Task.FromResult(response);
    };

    private const string RainyDays = """
        { "id": "u1", "name": "Rainy days", "description": "", "collection_type": "manual", "creator_profile_id": "7", "is_shared": true,
          "include_in_server_collections": false, "item_count": 4 }
        """;

    [Fact]
    public async Task OwnCards_OfferEdit_Sharing_AndDelete()
    {
        var page = await LoadedAsync();

        var manual = page.EntriesFor(page.Own[0]);
        Assert.Equal(["Edit collection", "Show to other profiles", "Delete…"], manual.Select(e => e.Label));
        Assert.Equal([null, true, null], manual.Select(e => e.IsChecked));
        Assert.Equal(["Show to other profiles", "Delete…"], page.EntriesFor(page.Own[1]).Select(e => e.Label));
        Assert.Empty(page.EntriesFor(page.Shared[0].Cards[0]));
    }

    [Fact]
    public async Task AnOwnSmartCollection_IsEditedToo()
    {
        _harness.Answer(PersonalPath, """
            { "items": [ { "id": "u9", "name": "90s Comedies", "collection_type": "smart", "creator_profile_id": "7", "profile_id": "7", "item_count": 40 } ] }
            """);
        var page = await LoadedAsync();

        Assert.Equal("Edit collection", page.EntriesFor(page.Own[0])[0].Label);
        page.EditCollectionCommand.Execute(page.Own[0]);

        Assert.Equal((AppRoute.CollectionEditor, (object?)new CollectionEditorRouteParameter("u9")), _harness.Navigation.Navigations[^1]);
    }

    [Fact]
    public async Task EditCollection_OpensTheEditor()
    {
        var page = await LoadedAsync();

        page.EditCollectionCommand.Execute(page.Own[0]);

        Assert.Equal((AppRoute.CollectionEditor, (object?)new CollectionEditorRouteParameter("u1")), _harness.Navigation.Navigations[^1]);
    }

    [Fact]
    public async Task StopSharing_AsksFirst_ThenSavesAtTheVersion()
    {
        _harness.Silo.On(HttpMethod.Get, "api/v2/collections/u1", Versioned(RainyDays, "\"r1\""));
        _harness.Silo.On(HttpMethod.Patch, "api/v2/collections/u1", Versioned(RainyDays.Replace("\"is_shared\": true", "\"is_shared\": false", StringComparison.Ordinal), "\"r2\""));
        var page = await LoadedAsync();

        await page.ToggleSharingCommand.ExecuteAsync(page.Own[0]);

        Assert.Equal(("Stop sharing Rainy days?", "Mara loses it, including rows they made from it.", "Stop sharing"), Assert.Single(_confirmation.Asked));
        var sent = Assert.Single(_harness.Silo.SentTo(HttpMethod.Patch, "api/v2/collections/u1"));
        Assert.Equal("\"r1\"", sent.IfMatch);
        Assert.False(JsonDocument.Parse(sent.Body!).RootElement.GetProperty("is_shared").GetBoolean());
        Assert.Equal("Only you see it now", page.Notice);
        Assert.Null(page.Own[0].StatusBadge);
        Assert.Equal([null, false, null], page.EntriesFor(page.Own[0]).Select(e => e.IsChecked));
    }

    [Fact]
    public async Task ACancelledQuestion_ChangesNothing()
    {
        _confirmation.Answer = false;
        var page = await LoadedAsync();

        await page.ToggleSharingCommand.ExecuteAsync(page.Own[0]);

        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Patch, "api/v2/collections/u1"));
        Assert.False(page.HasNotice);
    }

    [Fact]
    public async Task Delete_AsksAtTheCurrentVersion_AndTheCardGoes()
    {
        _harness.Silo.On(HttpMethod.Get, "api/v2/collections/u1", Versioned(RainyDays, "\"r1\""));
        _harness.Silo.On(HttpMethod.Delete, "api/v2/collections/u1", ScriptedSilo.NoContent());
        var page = await LoadedAsync();

        await page.DeleteCollectionCommand.ExecuteAsync(page.Own[0]);

        Assert.Equal("Delete “Rainy days”?", _confirmation.Asked[0].Title);
        Assert.Equal("It's removed for you and every profile you share it with. This can't be undone.", _confirmation.Asked[0].Message);
        Assert.Equal("\"r1\"", Assert.Single(_harness.Silo.SentTo(HttpMethod.Delete, "api/v2/collections/u1")).IfMatch);
        Assert.Equal(["Top Picks"], Titles(page.Own));
        Assert.Equal("Collection deleted", page.Notice);
    }
}
