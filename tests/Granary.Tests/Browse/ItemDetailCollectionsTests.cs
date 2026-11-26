using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Profiles;
using Granary.Core.ViewModels;
using Granary.Tests.Profiles;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

/// <summary>
/// The collections row of a movie's or series' page, as the web's <c>ItemCollectionsRow</c> shows it and the apps' rows behave
/// (silo-android cb2c32d8a, silo-apple fd83eb46).
/// </summary>
public sealed class ItemDetailCollectionsTests
{
    private const string CapabilitiesPath = "api/v2/catalog/search/capabilities";
    private const string ProfilesPath = "api/v2/profiles";

    private readonly ItemDetailFixture _fixture = new();

    public ItemDetailCollectionsTests()
    {
        _fixture.ItemCollections = new ItemCollectionsApi(_fixture.Silo.Http);
        _fixture.Profiles = new ProfilesApi(_fixture.Silo.Http);
        _fixture.Get(CapabilitiesPath, Ok(Capabilities(itemCollections: true)));
    }

    private static string CollectionsPath(string id) => $"api/v2/catalog/items/{id}/collections";

    private static string Capabilities(bool itemCollections) => $$"""
        { "revision": "r1", "state": "available", "allowed": true, "item_collections": {{(itemCollections ? "true" : "false")}} }
        """;

    private const string ServerCard = """
        { "id": "col-7", "source": "library", "title": "Crime classics", "poster_url": "/api/v2/images/collections/col-7/poster",
          "poster_is_collage": false, "item_count": 24, "library_id": "3" }
        """;

    private const string OwnCard = """
        { "id": "c1", "source": "user", "title": "Favorites", "poster_url": "/api/v2/images/user-collections/c1/collage",
          "poster_is_collage": true, "item_count": 7, "creator_profile_id": "7" }
        """;

    private const string SharedCard = """
        { "id": "c2", "source": "user", "title": "Sunday nights", "poster_url": "", "poster_is_collage": false, "item_count": 2,
          "creator_profile_id": "9" }
        """;

    private static string Answer(string cards, string preview = "") => $$"""{ "items": [{{cards}}], "preview_items": [{{preview}}] }""";

    private static string PreviewItem(string id, string title) => $$"""
        { "content_id": "{{id}}", "type": "movie", "title": "{{title}}", "year": 2020, "genres": [], "keywords": [], "status": "matched" }
        """;

    // Seven others and the title itself: the row keeps six, never the title's own.
    private static string SevenOthersAndThisOne() => string.Join(",",
        [PreviewItem(MovieId, "Masterplan"), .. Enumerable.Range(1, 7).Select(i => PreviewItem($"movie-tmdb-20{i}", $"Other {i}"))]);

    [Fact]
    public async Task OneCollection_ShowsUpToSixOfItsOtherTitles_UnderItsName()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer(ServerCard, SevenOthersAndThisOne())));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.True(detail.HasCollectionsRow);
        Assert.True(detail.IsCollectionPreview);
        Assert.Equal("Crime classics", detail.CollectionsRowTitle);
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"movie-tmdb-20{i}"), detail.CollectionsRow.Select(c => c.Id));
        Assert.NotNull(detail.CollectionsExploreAll);
    }

    [Fact]
    public async Task ExploreAll_OpensTheCollection_InItsLibrary()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer(ServerCard, PreviewItem("movie-tmdb-201", "Other"))));
        var detail = await _fixture.LoadAsync(MovieId);

        detail.CollectionsExploreAll!.Execute(null);

        Assert.Equal((AppRoute.Collection, (object?)new CollectionRouteParameter(CatalogSources.LibraryCollection, "col-7", "Crime classics", "3")),
            Assert.Single(_fixture.Navigation.Navigations));
    }

    [Fact]
    public async Task APreviewTitle_OpensItsOwnPage()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer(ServerCard, PreviewItem("movie-tmdb-201", "Other"))));
        var detail = await _fixture.LoadAsync(MovieId);

        detail.OpenCollectionsRowCardCommand.Execute(detail.CollectionsRow[0]);

        Assert.Equal((AppRoute.Item, (object?)"movie-tmdb-201"), Assert.Single(_fixture.Navigation.Navigations));
    }

    [Fact]
    public async Task SeveralCollections_ShowAnAlsoInRow_WithACardForEach()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer($"{ServerCard},{OwnCard},{SharedCard}")))
            .Get(ProfilesPath, Ok("""{ "items": [ { "id": "7", "name": "Me" }, { "id": "9", "name": "Mara" } ], "avatar_upload_enabled": false }"""));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.True(detail.HasCollectionsRow);
        Assert.False(detail.IsCollectionPreview);
        Assert.Equal("Also in", detail.CollectionsRowTitle);
        Assert.Null(detail.CollectionsExploreAll);
        Assert.Equal(["col-7", "c1", "c2"], detail.CollectionsRow.Select(c => c.Id));
        Assert.Equal(["Crime classics", "Favorites", "Sunday nights"], detail.CollectionsRow.Select(c => c.Title));
        // The web's CollectionPosterCard: a personal collection says so, another profile's names its owner; each its count.
        Assert.Equal(["24 titles", "User collection · 7 titles", "by Mara · 2 titles"], detail.CollectionsRow.Select(c => c.Subtitle));
        Assert.All(detail.CollectionsRow, c => Assert.True(c.PlainSubtitle));
    }

    [Fact]
    public async Task AnAlsoInCard_OpensItsCollection()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer($"{ServerCard},{OwnCard}")));
        var detail = await _fixture.LoadAsync(MovieId);

        detail.OpenCollectionsRowCardCommand.Execute(detail.CollectionsRow[0]);
        detail.OpenCollectionsRowCardCommand.Execute(detail.CollectionsRow[1]);

        Assert.Equal(
            [
                (AppRoute.Collection, (object?)new CollectionRouteParameter(CatalogSources.LibraryCollection, "col-7", "Crime classics", "3")),
                (AppRoute.Collection, (object?)new CollectionRouteParameter(CatalogSources.UserCollection, "c1", "Favorites")),
            ],
            _fixture.Navigation.Navigations);
        // Only the profile's own and the server's: no owner to name, so the profiles are not read.
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, ProfilesPath));
    }

    [Fact]
    public async Task ACardTheAlsoInRowDoesNotHold_OpensNothing()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer($"{ServerCard},{OwnCard}")));
        var detail = await _fixture.LoadAsync(MovieId);

        detail.OpenCollectionsRowCardCommand.Execute(detail.CollectionsRow[0] with { Title = "Another" });

        Assert.Empty(_fixture.Navigation.Navigations);
    }

    [Fact]
    public async Task TwoEqualCards_EachOpenTheirOwnLibrary()
    {
        // The same collection listed for two libraries: the cards look the same, but each opens where it was listed.
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer($"{ServerCard},{ServerCard.Replace("\"3\"", "\"4\"", StringComparison.Ordinal)}")));
        var detail = await _fixture.LoadAsync(MovieId);
        Assert.Equal(detail.CollectionsRow[0], detail.CollectionsRow[1]);

        detail.OpenCollectionsRowCardCommand.Execute(detail.CollectionsRow[1]);
        detail.OpenCollectionsRowCardCommand.Execute(detail.CollectionsRow[0]);

        Assert.Equal(["4", "3"], _fixture.Navigation.Navigations.Select(n => ((CollectionRouteParameter)n.Parameter!).LibraryId));
    }

    [Fact]
    public async Task AnUnreadableOwner_IsAnotherProfile()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer($"{SharedCard},{ServerCard}"))).Get(ProfilesPath, Busy());

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.Equal("by Another profile · 2 titles", detail.CollectionsRow[0].Subtitle);
    }

    [Fact]
    public async Task OneCollectionWithoutOtherTitles_ShowsItsCardUnderAlsoIn()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer(ServerCard)));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.False(detail.IsCollectionPreview);
        Assert.Equal("Also in", detail.CollectionsRowTitle);
        Assert.Equal(["col-7"], detail.CollectionsRow.Select(c => c.Id));
    }

    [Fact]
    public async Task ASeriesAsks_AsAMovieDoes()
    {
        _fixture.Series().Get(CollectionsPath(SeriesId), Ok(Answer($"{ServerCard},{OwnCard}")));

        var detail = await _fixture.LoadAsync(SeriesId);

        Assert.True(detail.HasCollectionsRow);
    }

    [Fact]
    public async Task ASeasonOrEpisode_DoesNotAsk()
    {
        _fixture.Season().Episode();

        var season = await _fixture.LoadAsync(Season3Id);
        var episode = await _fixture.LoadAsync(EpisodeId);

        Assert.False(season.HasCollectionsRow);
        Assert.False(episode.HasCollectionsRow);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, CapabilitiesPath));
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, CollectionsPath(Season3Id)));
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, CollectionsPath(EpisodeId)));
    }

    [Fact]
    public async Task AServerWithoutTheCapability_IsNotAsked()
    {
        _fixture.Movie().Get(CapabilitiesPath, Ok(Capabilities(itemCollections: false)));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.False(detail.HasCollectionsRow);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, CollectionsPath(MovieId)));
    }

    [Fact]
    public async Task AnOlderServer_WhoseCapabilitiesLackIt_IsNotAsked()
    {
        _fixture.Movie().Get(CapabilitiesPath, Ok("""{ "revision": "r1", "state": "available", "allowed": true }"""));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.False(detail.HasCollectionsRow);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, CollectionsPath(MovieId)));
    }

    [Fact]
    public async Task UnreadableCapabilities_ShowNoRow_AndLeaveThePage()
    {
        _fixture.Movie().Get(CapabilitiesPath, Busy());

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.Equal(ItemDetailState.Ready, detail.State);
        Assert.True(detail.HasSimilar);
        Assert.False(detail.HasCollectionsRow);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, CollectionsPath(MovieId)));
    }

    [Fact]
    public async Task NoCollection_ShowsNoRow()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok("""{ "items": [], "preview_items": [] }"""));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.False(detail.HasCollectionsRow);
        Assert.Empty(detail.CollectionsRow);
    }

    [Fact]
    public async Task AFailedFirstRead_ShowsNoRow_AndLeavesTheOtherRows()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Busy());

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.Equal(ItemDetailState.Ready, detail.State);
        Assert.False(detail.HasCollectionsRow);
        Assert.True(detail.HasSimilar);
        Assert.False(detail.HasActionError);
    }

    [Fact]
    public async Task ARefresh_ShowsTheNewAnswer()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer(ServerCard)), Ok(Answer($"{ServerCard},{OwnCard}")));
        var detail = await _fixture.LoadAsync(MovieId);
        Assert.Single(detail.CollectionsRow);

        await detail.RefreshCollectionsAsync();

        Assert.Equal(["col-7", "c1"], detail.CollectionsRow.Select(c => c.Id));
    }

    [Fact]
    public async Task AFailedRefresh_KeepsTheRow()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer($"{ServerCard},{OwnCard}")), Busy());
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.RefreshCollectionsAsync();

        Assert.Equal(["col-7", "c1"], detail.CollectionsRow.Select(c => c.Id));
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, CollectionsPath(MovieId)).Count);
    }

    [Fact]
    public async Task ARefreshWithUnreadableCapabilities_KeepsTheRow()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer($"{ServerCard},{OwnCard}")))
            .Get(CapabilitiesPath, Ok(Capabilities(itemCollections: true)), Busy());
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.RefreshCollectionsAsync();

        Assert.True(detail.HasCollectionsRow);
        // The refresh asked for the capabilities again and stopped there.
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, CapabilitiesPath).Count);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, CollectionsPath(MovieId)));
    }

    [Fact]
    public async Task ARefreshThatFindsNoneLeft_RemovesTheRow()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer(ServerCard)), Ok("""{ "items": [], "preview_items": [] }"""));
        var detail = await _fixture.LoadAsync(MovieId);

        await detail.RefreshCollectionsAsync();

        Assert.False(detail.HasCollectionsRow);
    }

    [Fact]
    public async Task AnotherTitle_DropsTheLastOnesRow()
    {
        _fixture.Movie().Get(CollectionsPath(MovieId), Ok(Answer(ServerCard))).Series().Get(CollectionsPath(SeriesId), Busy());
        var detail = _fixture.Create();
        await detail.LoadCommand.ExecuteAsync(MovieId);
        Assert.True(detail.HasCollectionsRow);

        await detail.LoadCommand.ExecuteAsync(SeriesId);

        Assert.False(detail.HasCollectionsRow);
        Assert.Equal("", detail.CollectionsRowTitle);
    }

    [Fact]
    public async Task ARefreshBeforeTheTitleIsRead_AsksNothing()
    {
        var detail = _fixture.Create();

        await detail.RefreshCollectionsAsync();

        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, CapabilitiesPath));
    }
}
