using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Tests.Profiles;

namespace Granary.Tests.Catalog;

/// <summary>Method, path, query and credentials of every call, against a server behind the path prefix <c>/silo</c>.</summary>
public sealed class CatalogApiTests
{
    private static readonly Uri Server = ScriptedSilo.Base;

    private readonly ScriptedSilo _silo = new();

    private static readonly ImageParameters Images = ImageSizes.Default.For(
        JsonSerializer.Deserialize(CatalogPayloads.ImageCapabilities, SiloJsonContext.Default.ImageCapabilities));

    private CatalogApi Api => new(_silo.Http);

    private SentRequest Only(SiloAuth auth = SiloAuth.Profile)
    {
        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(auth, sent.Auth);
        return sent;
    }

    private void Answer(HttpMethod method, string path, string json) => _silo.On(method, path, ScriptedSilo.Json(HttpStatusCode.OK, json));

    private void AnswerNoContent(HttpMethod method, string path) => _silo.On(method, path, ScriptedSilo.NoContent());

    [Fact]
    public async Task ListHomeSections_SendsTheImageSizes()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Get, "api/v2/home/sections", CatalogPayloads.HomeSections);

        var sections = await Api.ListHomeSectionsAsync(Server, Images, ct);

        Assert.Single(sections.Sections);
        Assert.Equal("/silo/api/v2/home/sections?poster_width=342&backdrop_width=1280&logo_width=500&still_width=400&photo_width=185",
            Only().Uri.PathAndQuery);
    }

    [Fact]
    public async Task GetHomeSectionItems_GetsOneRow()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Get, "api/v2/home/sections/default-recently-added-tv/items", CatalogPayloads.RecentlyAddedSection);

        var row = await Api.GetHomeSectionItemsAsync(Server, "default-recently-added-tv", cancellationToken: ct);

        Assert.Equal(3, row.Items.Count);
        Assert.Equal("/silo/api/v2/home/sections/default-recently-added-tv/items", Only().Uri.PathAndQuery);
    }

    [Fact]
    public async Task DismissInProgressCard_LeavesContinueWatchingAnchoredToItsProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var sections = JsonSerializer.Deserialize(CatalogPayloads.HomeSections, SiloJsonContext.Default.SectionCollection)!;
        var card = sections.Sections[0].Items[0];
        AnswerNoContent(HttpMethod.Put, "api/v2/home/dismissals/continue_watching/movie%3Aheat-1995");

        var dismissal = HomeDismissal.For(card)!;
        await Api.DismissHomeItemAsync(Server, dismissal, ct);

        var sent = Only();
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("/silo/api/v2/home/dismissals/continue_watching/movie%3Aheat-1995", sent.Uri.PathAndQuery);
        Assert.Equal("""{"progress_updated_at":"2026-01-02T03:04:05+00:00"}""", sent.Body);
    }

    [Fact]
    public async Task DismissNextUpCard_LeavesNextUpWithItsSeries()
    {
        var ct = TestContext.Current.CancellationToken;
        var row = JsonSerializer.Deserialize(CatalogPayloads.RecentlyAddedSection, SiloJsonContext.Default.Section)!;
        AnswerNoContent(HttpMethod.Put, "api/v2/home/dismissals/next_up/episode-tvdb-296762-1-5");

        var dismissal = HomeDismissal.For(row.Items[0])!;
        await Api.DismissHomeItemAsync(Server, dismissal, ct);

        Assert.Equal(new HomeDismissal(HomeSurfaces.NextUp, "episode-tvdb-296762-1-5", null, "series-tvdb-296762"), dismissal);
        Assert.Equal("""{"series_id":"series-tvdb-296762"}""", Only().Body);
    }

    [Fact]
    public void CardWithoutProgressOrSeries_HasNoRemoveAction()
    {
        var row = JsonSerializer.Deserialize(CatalogPayloads.RecentlyAddedSection, SiloJsonContext.Default.Section)!;

        Assert.Null(HomeDismissal.For(row.Items[2]));
        Assert.Equal(HomeSurfaces.NextUp, HomeDismissal.For(row.Items[1] with { SeriesId = "s" })?.Surface);
    }

    [Fact]
    public async Task Undismiss_DeletesWithoutABody()
    {
        var ct = TestContext.Current.CancellationToken;
        AnswerNoContent(HttpMethod.Delete, "api/v2/home/dismissals/next_up/episode-1");

        await Api.UndismissHomeItemAsync(Server, new HomeDismissal(HomeSurfaces.NextUp, "episode-1", null, "series-1"), ct);

        var sent = Only();
        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Null(sent.Body);
    }

    [Fact]
    public async Task Libraries_AreListedForTheProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Get, "api/v2/user/libraries", CatalogPayloads.Libraries);

        var libraries = await Api.ListLibrariesAsync(Server, ct);

        Assert.Equal("Movies", Assert.Single(libraries.Items).Name);
        Assert.Equal("/silo/api/v2/user/libraries", Only().Uri.PathAndQuery);
    }

    [Fact]
    public async Task LibrarySections_AreListedPerLibrary()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Get, "api/v2/library/12/sections", CatalogPayloads.HomeSections);
        Answer(HttpMethod.Get, "api/v2/library/12/sections/recent%20films/items", CatalogPayloads.RecentlyAddedSection);

        await Api.ListLibrarySectionsAsync(Server, "12", Images, ct);
        await Api.GetLibrarySectionItemsAsync(Server, "12", "recent films", cancellationToken: ct);

        Assert.Equal(["/silo/api/v2/library/12/sections?poster_width=342&backdrop_width=1280&logo_width=500&still_width=400&photo_width=185",
            "/silo/api/v2/library/12/sections/recent%20films/items"], _silo.Sent.Select(s => s.Uri.PathAndQuery));
        Assert.All(_silo.Sent, s => Assert.Equal(SiloAuth.Profile, s.Auth));
    }

    [Fact]
    public async Task Browse_SendsTheQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Get, "api/v2/catalog", CatalogPayloads.BrowsePage);
        var query = new CatalogQuery { LibraryId = "12", Type = MediaScopes.Movie, Sort = CatalogSort.Descending(CatalogSortFields.AddedAt), Limit = 42 };

        var page = await Api.BrowseAsync(Server, query, ct);
        await Api.BrowseAsync(Server, query.WithCursor(page.NextCursor), ct);

        Assert.Equal(["/silo/api/v2/catalog?library_id=12&type=movie&sort=-added_at&limit=42",
            "/silo/api/v2/catalog?library_id=12&type=movie&sort=-added_at&limit=42&cursor=eyJ2IjoxLCJwIjp7Im8iOjJ9fQ.48NB"],
            _silo.Sent.Select(s => s.Uri.PathAndQuery));
    }

    [Fact]
    public async Task Letters_IndexTheQuerysScopeFiltersAndOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Post, "api/v2/catalog/letters", """
            { "total": 12, "letters": [ { "letter": "#", "count": 2, "position": 0 }, { "letter": "H", "count": 10, "position": 2 } ],
              "runs": [ { "letter": "#", "position": 0 }, { "letter": "H", "position": 2 } ] }
            """);
        var query = new CatalogQuery
        {
            LibraryId = "12", Type = MediaScopes.Movie, Genre = " Crime ", YearMax = 1999, Sort = CatalogSort.Descending(CatalogSortFields.Title),
            Limit = 60, Cursor = "c", Images = Images,
        };

        var letters = await Api.GetLettersAsync(Server, query, ct);

        var sent = Only();
        Assert.Equal("/silo/api/v2/catalog/letters", sent.Uri.PathAndQuery);
        Assert.Equal("""
            {"library_id":"12","type":"movie","groups":[{"match":"all","rules":[{"field":"genre","op":"contains","value":"Crime"},{"field":"year","op":"lte","value":1999}]}],"order":"desc"}
            """, sent.Body);
        Assert.Equal(12, letters.Total);
        Assert.Equal(new CatalogLetter("H", 10, 2), letters.Letters[1]);
        Assert.Equal("H", letters.LetterAt(5));
    }

    [Fact]
    public async Task Browse_RefusesARefusedQueryBeforeSending()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Api.BrowseAsync(Server, CatalogQuery.ForHomeSection("s1") with { Q = "heat" }, TestContext.Current.CancellationToken));

        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task Filters_SendOnlyTheScopeOfTheQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Get, "api/v2/catalog/filters", CatalogPayloads.Filters);
        var query = new CatalogQuery { LibraryId = "12", Type = MediaScopes.Series, Genre = "Drama", Sort = CatalogSort.Ascending("title"), Cursor = "c" };

        var filters = await Api.GetFiltersAsync(Server, query, cancellationToken: ct);
        await Api.GetFiltersAsync(Server, query, includeTechnical: true, ct);

        Assert.Equal(["Crime"], filters.Genres);
        Assert.Equal(["/silo/api/v2/catalog/filters?library_id=12&type=series&skip_technical=true",
            "/silo/api/v2/catalog/filters?library_id=12&type=series"], _silo.Sent.Select(s => s.Uri.PathAndQuery));
    }

    [Fact]
    public async Task Filters_OfASectionListing_NameTheSection()
    {
        Answer(HttpMethod.Get, "api/v2/catalog/filters", CatalogPayloads.Filters);

        await Api.GetFiltersAsync(Server, CatalogQuery.ForLibrarySection("12", "recent"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/silo/api/v2/catalog/filters?source=section&scope=library&section_id=recent&library_id=12&skip_technical=true",
            Only().Uri.PathAndQuery);
    }

    [Fact]
    public async Task GetItem_EscapesTheIdAndNamesTheLibrary()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Get, "api/v2/catalog/items/movie%3Aheat-1995", CatalogPayloads.ItemFixture);

        var item = await Api.GetItemAsync(Server, "movie:heat-1995", "12", cancellationToken: ct);

        Assert.Equal("Heat", item.Title);
        Assert.Equal("/silo/api/v2/catalog/items/movie%3Aheat-1995?library_id=12", Only().Uri.PathAndQuery);
    }

    [Fact]
    public async Task GetItem_ReportsAMissingItem()
    {
        _silo.On(HttpMethod.Get, "api/v2/catalog/items/movie-1", ScriptedSilo.Problem(404, "not_found"));

        var error = await Assert.ThrowsAsync<SiloApiException>(() => Api.GetItemAsync(Server, "movie-1", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal((404, "not_found"), (error.Status, error.Code));
    }

    [Fact]
    public async Task SeasonsAndEpisodes_UseTheirPaths()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Get, "api/v2/catalog/series/series-tvdb-296762/seasons", CatalogPayloads.Seasons);
        Answer(HttpMethod.Get, "api/v2/catalog/series/series-tvdb-296762/seasons/0/episodes", CatalogPayloads.Episodes);
        Answer(HttpMethod.Get, "api/v2/catalog/items/season-tvdb-296762-1/episodes", CatalogPayloads.Episodes);

        var seasons = await Api.ListSeasonsAsync(Server, "series-tvdb-296762", cancellationToken: ct);
        var specials = await Api.ListEpisodesAsync(Server, "series-tvdb-296762", 0, "12", cancellationToken: ct);
        var season = await Api.ListSeasonEpisodesAsync(Server, "season-tvdb-296762-1", images: Images, cancellationToken: ct);

        Assert.Single(seasons.Items);
        Assert.Equal(2, specials.Items.Count);
        Assert.Equal(2, season.Items.Count);
        Assert.Equal(["/silo/api/v2/catalog/series/series-tvdb-296762/seasons",
            "/silo/api/v2/catalog/series/series-tvdb-296762/seasons/0/episodes?library_id=12",
            "/silo/api/v2/catalog/items/season-tvdb-296762-1/episodes?poster_width=342&backdrop_width=1280&logo_width=500&still_width=400&photo_width=185"],
            _silo.Sent.Select(s => s.Uri.PathAndQuery));
        Assert.All(_silo.Sent, s => Assert.Equal(SiloAuth.Profile, s.Auth));
    }

    [Fact]
    public async Task Similar_ListsMoreLikeThis()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Get, "api/v2/recommendations/similar/movie%3Aheat-1995", CatalogPayloads.Similar);

        var similar = await Api.ListSimilarAsync(Server, "movie:heat-1995", 12, cancellationToken: ct);

        Assert.Single(similar.Items);
        Assert.Equal("/silo/api/v2/recommendations/similar/movie%3Aheat-1995?limit=12", Only().Uri.PathAndQuery);
    }

    [Fact]
    public async Task Similar_WithoutALimit_LeavesTheServersDefault()
    {
        Answer(HttpMethod.Get, "api/v2/recommendations/similar/movie-1", CatalogPayloads.Similar);

        await Api.ListSimilarAsync(Server, "movie-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/silo/api/v2/recommendations/similar/movie-1", Only().Uri.PathAndQuery);
    }

    [Theory]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("a?b#c", "a%3Fb%23c")]
    [InlineData("100%", "100%25")]
    [InlineData("local-9f3a", "local-9f3a")]
    public async Task Ids_AreEscapedAsOneSegment(string id, string segment)
    {
        Answer(HttpMethod.Get, "api/v2/catalog/items/" + segment, CatalogPayloads.ItemFixture);

        await Api.GetItemAsync(Server, id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/silo/api/v2/catalog/items/" + segment, Only().Uri.AbsolutePath);
    }

    [Fact]
    public async Task Search_ReadsCapabilitiesAndPeople()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Get, "api/v2/catalog/search/capabilities", CatalogPayloads.SearchCapabilities);
        Answer(HttpMethod.Get, "api/v2/catalog/people", CatalogPayloads.People);

        var capabilities = await Api.GetSearchCapabilitiesAsync(Server, ct);
        var people = await Api.SearchPeopleAsync(Server, "al pa", 20, MediaScopes.Video, ct);

        Assert.True(capabilities.PeopleMediaScope);
        Assert.Single(people.Items);
        Assert.Equal(["/silo/api/v2/catalog/search/capabilities", "/silo/api/v2/catalog/people?q=al%20pa&limit=20&media_scope=video"],
            _silo.Sent.Select(s => s.Uri.PathAndQuery));
        Assert.All(_silo.Sent, s => Assert.Equal(SiloAuth.Profile, s.Auth));
    }

    [Theory]
    [InlineData(true, "PUT")]
    [InlineData(false, "DELETE")]
    public async Task Favorite_PutsOrDeletes(bool isFavorite, string method)
    {
        AnswerNoContent(new HttpMethod(method), "api/v2/favorites/movie%3Aheat-1995");

        await Api.SetFavoriteAsync(Server, "movie:heat-1995", isFavorite, TestContext.Current.CancellationToken);

        var sent = Only();
        Assert.Equal(method, sent.Method.Method);
        Assert.Equal("/silo/api/v2/favorites/movie%3Aheat-1995", sent.Uri.PathAndQuery);
        Assert.Null(sent.Body);
    }

    [Theory]
    [InlineData(true, "PUT")]
    [InlineData(false, "DELETE")]
    public async Task Watchlist_PutsOrDeletes(bool inWatchlist, string method)
    {
        AnswerNoContent(new HttpMethod(method), "api/v2/watchlist/series-1");

        await Api.SetWatchlistAsync(Server, "series-1", inWatchlist, TestContext.Current.CancellationToken);

        Assert.Equal(method, Only().Method.Method);
    }

    [Theory]
    [InlineData(true, "POST")]
    [InlineData(false, "DELETE")]
    public async Task Watched_PostsOrDeletes(bool watched, string method)
    {
        AnswerNoContent(new HttpMethod(method), "api/v2/watched/season-1");

        await Api.SetWatchedAsync(Server, "season-1", watched, TestContext.Current.CancellationToken);

        var sent = Only();
        Assert.Equal(method, sent.Method.Method);
        Assert.Equal("/silo/api/v2/watched/season-1", sent.Uri.PathAndQuery);
    }

    [Fact]
    public async Task ImageCapabilities_AreReadWithTheProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        Answer(HttpMethod.Get, "api/v2/images/capabilities", CatalogPayloads.ImageCapabilities);

        var capabilities = await new ImagesApi(_silo.Http).GetCapabilitiesAsync(Server, ct);

        Assert.True(capabilities.IsAvailable);
        Assert.Equal("/silo/api/v2/images/capabilities", Only().Uri.PathAndQuery);
    }

    [Fact]
    public async Task InvalidArguments_AreRefusedBeforeSending()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => Api.GetItemAsync(Server, " ", cancellationToken: ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Api.GetItemAsync(Server, "..", cancellationToken: ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Api.GetHomeSectionItemsAsync(Server, ".", cancellationToken: ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Api.SetWatchedAsync(Server, "..", true, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Api.ListEpisodesAsync(Server, "series-1", -1, cancellationToken: ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Api.ListSimilarAsync(Server, "movie-1", 51, cancellationToken: ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Api.SearchPeopleAsync(Server, "al", 0, cancellationToken: ct));

        Assert.Empty(_silo.Sent);
    }
}
