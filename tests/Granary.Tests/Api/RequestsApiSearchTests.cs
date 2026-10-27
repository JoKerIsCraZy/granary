using System.Net;
using System.Web;
using Granary.Core.Api;
using Granary.Core.Api.Requests;
using Granary.Tests.Profiles;

namespace Granary.Tests.Api;

/// <summary>The request search Search's "Request to add" reads, as Silo's web sends it (searchRequestMediaV2).</summary>
public sealed class RequestsApiSearchTests
{
    private const string SearchPath = "api/v2/requests/search";

    private readonly ScriptedSilo _silo = new();

    private RequestsApi Api => new(_silo.Http);

    [Fact]
    public async Task Search_SendsTheTextTypeAndPage_AsTheProfile_AndReadsThePage()
    {
        _silo.On(HttpMethod.Get, SearchPath, ScriptedSilo.Json(HttpStatusCode.OK, """
            { "page": 2, "total_pages": 9, "total_results": 170,
              "results": [ { "tmdb_id": 949, "media_type": "movie", "title": "Heat", "year": 1995, "poster_path": "/heat.jpg",
                             "availability": "none", "in_watchlist": true,
                             "request": { "requestable": true, "following": false, "requested_by_viewer": false } } ] }
            """));

        var page = await Api.SearchAsync(ScriptedSilo.Base, " heat & dust ", "movie", 2, TestContext.Current.CancellationToken);

        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(SiloAuth.Profile, sent.Auth);
        var query = HttpUtility.ParseQueryString(sent.Uri.Query);
        Assert.Equal(("heat & dust", "movie", "2"), (query["q"], query["media_type"], query["page"]));
        Assert.Equal((2, 9, 170), (page.Page, page.TotalPages, page.TotalResults));
        var title = Assert.Single(page.Results);
        Assert.Equal((949L, "movie", "Heat", 1995, "none", true), (title.TmdbId, title.MediaType, title.Title, title.Year, title.Availability, title.InWatchlist));
        Assert.True(title.Request.Requestable);
    }

    [Fact]
    public async Task Search_ReadsAPageWithoutResults()
    {
        _silo.On(HttpMethod.Get, SearchPath, ScriptedSilo.Json(HttpStatusCode.OK, """{ "page": 1, "total_pages": 0, "total_results": 0, "results": null }"""));

        var page = await Api.SearchAsync(ScriptedSilo.Base, "zzzz", "all", 1, TestContext.Current.CancellationToken);

        Assert.Empty(page.Results);
        Assert.Equal(0, page.TotalPages);
    }

    [Theory]
    [InlineData("", "all", 1)]
    [InlineData("  ", "all", 1)]
    [InlineData("heat", "episode", 1)]
    [InlineData("heat", "all", 0)]
    [InlineData("heat", "all", RequestsApi.TmdbMaxPage + 1)]
    public async Task Search_RefusesWhatTheServerWouldNot_WithoutSending(string query, string mediaType, int page)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Api.SearchAsync(ScriptedSilo.Base, query, mediaType, page, TestContext.Current.CancellationToken));

        Assert.Empty(_silo.Sent);
    }
}
