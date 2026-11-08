using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Tests.Browse;
using Granary.Tests.Profiles;

namespace Granary.Tests.Catalog;

/// <summary>The three box set reads: their paths, queries and answers, against a server behind the path prefix <c>/silo</c>.</summary>
public sealed class BoxSetsApiTests
{
    private const string ItemPath = "api/v2/catalog/items/movie-tmdb-1001/box-set";
    private const string BoxSetPath = "api/v2/catalog/box-sets/1241";
    private const string SearchPath = "api/v2/catalog/box-sets";

    private static readonly Uri Server = ScriptedSilo.Base;

    private static readonly ImageParameters Images = ImageSizes.Default.For(
        JsonSerializer.Deserialize(CatalogPayloads.ImageCapabilities, SiloJsonContext.Default.ImageCapabilities));

    private readonly ScriptedSilo _silo = new();

    private BoxSetsApi Api => new(_silo.Http);

    private SentRequest Only()
    {
        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(SiloAuth.Profile, sent.Auth);
        return sent;
    }

    [Fact]
    public async Task AMoviesBoxSet_IsReadWithItsFilmsInReleaseOrder()
    {
        _silo.On(HttpMethod.Get, ItemPath, ScriptedSilo.Json(HttpStatusCode.OK, BoxSetPayloads.ItemBoxSet));

        var boxSet = await Api.GetForItemAsync(Server, ItemDetailPayloads.MovieId, images: Images, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/silo/api/v2/catalog/items/movie-tmdb-1001/box-set?poster_width=342&backdrop_width=1280&logo_width=500&still_width=400&photo_width=185",
            Only().Uri.PathAndQuery);
        Assert.NotNull(boxSet);
        Assert.Equal(("1241", "3", "Masterplan Box Set"), (boxSet.Id, boxSet.LibraryId, boxSet.Title));
        Assert.Equal((3, 1, 2019, 2028), (boxSet.MovieCount, boxSet.PlayedCount, boxSet.FirstYear, boxSet.LastYear));
        Assert.Equal(["Groundwork", "Masterplan", "Endgame"], boxSet.Items.Select(i => i.Title));
        Assert.True(boxSet.Items[0].UserState?.Played);
    }

    [Fact]
    public async Task AMovieInNoBoxSet_AnswersNone()
    {
        _silo.On(HttpMethod.Get, ItemPath, ScriptedSilo.Json(HttpStatusCode.OK, BoxSetPayloads.NoBoxSet));

        var boxSet = await Api.GetForItemAsync(Server, ItemDetailPayloads.MovieId, libraryId: "3", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(boxSet);
        Assert.Equal("/silo/api/v2/catalog/items/movie-tmdb-1001/box-set?library_id=3", Only().Uri.PathAndQuery);
    }

    [Fact]
    public async Task ABoxSet_IsReadInItsLibrary()
    {
        _silo.On(HttpMethod.Get, BoxSetPath, ScriptedSilo.Json(HttpStatusCode.OK, BoxSetPayloads.BoxSet()));

        var boxSet = await Api.GetAsync(Server, "1241", "3", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/silo/api/v2/catalog/box-sets/1241?library_id=3", Only().Uri.PathAndQuery);
        Assert.Equal("Three jobs, one crew.", boxSet.Overview);
        Assert.Equal(3, boxSet.Items.Count);
    }

    [Fact]
    public async Task Search_AsksForFiveBoxSetsAsTheWebDoes()
    {
        _silo.On(HttpMethod.Get, SearchPath, ScriptedSilo.Json(HttpStatusCode.OK, BoxSetPayloads.SearchList));

        var found = await Api.SearchAsync(Server, " masterplan ", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("/silo/api/v2/catalog/box-sets?q=masterplan&limit=5", Only().Uri.PathAndQuery);
        Assert.Equal(["Masterplan Box Set", "Groundwork Collection"], found.Select(b => b.Title));
        Assert.Null(found[1].PosterUrl);
    }

    [Fact]
    public async Task AServerWithoutBoxSets_AnswersNotFound()
    {
        _silo.On(HttpMethod.Get, ItemPath, ScriptedSilo.Problem(404, "not_found"));

        var failure = await Assert.ThrowsAsync<SiloApiException>(() =>
            Api.GetForItemAsync(Server, ItemDetailPayloads.MovieId, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(404, failure.Status);
    }
}
