using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Requests;
using Granary.Core.ViewModels;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>A Discover row in full, and a studio's, network's or genre's titles, as Silo's web shows them.</summary>
public sealed class RequestTitlesViewModelTests
{
    private const string SectionPath = "api/v2/requests/discover/trending_movies";
    private const string StudioPath = "api/v2/requests/discover/browse/studio/marvel-studios";
    private const string GenrePath = "api/v2/requests/discover/browse/genre/action";
    private const string Logo = "https://image.tmdb.org/t/p/w780_filter(duotone,ffffff,bababa)/marvel.png";

    private static readonly RequestTitlesRouteParameter Row = new("trending_movies", "Trending Movies");
    private static readonly RequestTitlesRouteParameter Studio = new("marvel-studios", "Marvel Studios", DiscoverBrowseKind.Studio);
    private static readonly RequestTitlesRouteParameter Genre = new("action", "Action", DiscoverBrowseKind.Genre, "movie");

    private readonly LibraryHarness _harness = new();

    private RequestTitlesViewModel Create() =>
        new(_harness.Session, new RequestsApi(_harness.Silo.Http), _harness.Navigation, _harness.Logs.For<RequestTitlesViewModel>());

    private async Task<RequestTitlesViewModel> ShownAsync(RequestTitlesRouteParameter route)
    {
        var titles = Create();
        await titles.ShowAsync(route);
        return titles;
    }

    /// <summary>A page of a Discover row with movies of these TMDB ids ("Movie 10").</summary>
    private static string Section(int page, int totalPages, int[] ids, int? nextPage = null, string title = "Trending Movies") =>
        $$"""
        { "key": "trending_movies", "title": "{{title}}", "page": {{page}}, "total_pages": {{totalPages}}, "total_results": 400,
          {{(nextPage is int next ? $"\"next_page\": {next}," : "")}} "results": [{{string.Join(",", ids.Select(Result))}}] }
        """;

    private static string Browse(int page, int totalPages, int[] ids, string name = "Marvel Studios", string logo = Logo) =>
        $$"""
        { "kind": "studio", "slug": "marvel-studios", "display_name": "{{name}}", "logo_url": "{{logo}}", "media_type": "movie",
          "sort": "popularity", "page": {{page}}, "total_pages": {{totalPages}}, "results": [{{string.Join(",", ids.Select(Result))}}] }
        """;

    private static string Result(int id) => id == 99
        ? """
          { "tmdb_id": 99, "media_type": "movie", "title": "Movie 99", "year": 2020, "availability": "available", "library_content_id": "movie:m99",
            "in_watchlist": false, "request": { "requestable": false, "reason": "already_available", "following": false, "requested_by_viewer": false } }
          """
        : $$"""
          { "tmdb_id": {{id}}, "media_type": "movie", "title": "Movie {{id}}", "year": 2020, "poster_path": "/m{{id}}.jpg", "availability": "none",
            "in_watchlist": false, "request": { "requestable": true, "following": false, "requested_by_viewer": false } }
          """;

    private static string[] Titles(RequestTitlesViewModel titles) => [.. titles.Cards.Select(c => c.Title)];

    [Fact]
    public async Task ARow_ShowsItsFirstPage_UnderTheServersName()
    {
        _harness.Answer(SectionPath, Section(1, 5, [10, 11], title: "Trending This Week"));

        var titles = await ShownAsync(Row with { Title = "" });

        Assert.True(titles.IsReady);
        Assert.Equal("Trending This Week", titles.Title);
        Assert.Equal(["Movie 10", "Movie 11"], Titles(titles));
        Assert.Equal(new Uri("https://image.tmdb.org/t/p/w342/m10.jpg"), titles.Cards[0].ImageUrl);
        Assert.Equal(["page=1"], _harness.Queries(SectionPath));
        Assert.True(titles.CanLoadMore);
        Assert.Null(titles.Browse);
        Assert.False(titles.HasChoices);
    }

    [Fact]
    public async Task WhileItLoads_TheRowIsNamedByItsKey()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(SectionPath, LibraryHarness.Held(release, Section(1, 1, [10])));
        var titles = Create();

        var shown = titles.ShowAsync(Row with { Title = "" });
        Assert.Equal("Trending movies", titles.Title);
        Assert.True(titles.IsLoading);
        release.SetResult();
        await shown;

        Assert.Equal("Trending Movies", titles.Title);
    }

    [Fact]
    public async Task MorePages_AddTheirNewTitles_UntilTheLast()
    {
        _harness.Answer(SectionPath, Section(1, 2, [10, 11]), Section(2, 2, [11, 12]));
        var titles = await ShownAsync(Row);

        await titles.LoadMoreCommand.ExecuteAsync(null);

        // TMDB lists shift between reads: a title an earlier page showed shows once.
        Assert.Equal(["Movie 10", "Movie 11", "Movie 12"], Titles(titles));
        Assert.Equal(["page=1", "page=2"], _harness.Queries(SectionPath));
        Assert.False(titles.CanLoadMore);
        Assert.False(titles.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARowThatNamesItsNextPage_IsReadThere_AndEndsWithoutOne()
    {
        // A rating limit made the server read pages 1–3 to fill the first.
        _harness.Answer(SectionPath, Section(1, 40, [10], nextPage: 4), Section(4, 40, [11]));
        var titles = await ShownAsync(Row);

        await titles.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(["page=1", "page=4"], _harness.Queries(SectionPath));
        Assert.False(titles.CanLoadMore);
    }

    [Fact]
    public async Task TmdbsLastPage_IsTheLast_WhateverTheTotal()
    {
        _harness.Answer(SectionPath, Section(1, 900, [10], nextPage: 501));

        var titles = await ShownAsync(Row);

        Assert.False(titles.CanLoadMore);
    }

    [Fact]
    public async Task PagesThatAddNothing_ReadTheNext_AFewTimesInARow()
    {
        _harness.Answer(SectionPath, Section(1, 10, []), Section(2, 10, []), Section(3, 10, []), Section(4, 10, [10]));

        var titles = await ShownAsync(Row);

        Assert.Equal(["page=1", "page=2", "page=3"], _harness.Queries(SectionPath));
        Assert.True(titles.IsReady);
        Assert.Empty(titles.Cards);
        Assert.True(titles.ShowsLoadMore);

        await titles.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(["Movie 10"], Titles(titles));
    }

    [Fact]
    public async Task ACursorThatDoesNotMoveOn_EndsTheRow()
    {
        _harness.Answer(SectionPath, Section(1, 40, [10], nextPage: 1));

        var titles = await ShownAsync(Row);

        Assert.False(titles.CanLoadMore);
    }

    [Fact]
    public async Task WhileAPageLoads_ItIsNotAskedForAgain()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(SectionPath, ScriptedSilo.Json(HttpStatusCode.OK, Section(1, 3, [10])), LibraryHarness.Held(release, Section(2, 3, [11])));
        var titles = await ShownAsync(Row);

        var more = titles.LoadMoreCommand.ExecuteAsync(null);

        Assert.True(titles.IsLoadingMore);
        Assert.False(titles.ShowsLoadMore);
        Assert.False(titles.LoadMoreCommand.CanExecute(null));
        release.SetResult();
        await more;
        Assert.Equal(["page=1", "page=2"], _harness.Queries(SectionPath));
        Assert.True(titles.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task ANewLoad_ShowsOnlyItsOwnAnswer()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(SectionPath, LibraryHarness.Held(release, Section(1, 40, [10], nextPage: 4)), ScriptedSilo.Json(HttpStatusCode.OK, Section(1, 5, [20])),
            ScriptedSilo.Json(HttpStatusCode.OK, Section(2, 5, [21])));
        var titles = Create();

        var first = titles.ShowAsync(Row);
        await titles.ShowAsync(Row);
        release.SetResult();
        await first;
        await titles.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(["Movie 20", "Movie 21"], Titles(titles));
        Assert.Equal("page=2", _harness.Queries(SectionPath)[^1]);
    }

    [Fact]
    public async Task BackToAPageThatFailed_ReadsItAgain()
    {
        _harness.Answer(SectionPath, ScriptedSilo.Problem(502, "bad_gateway"), ScriptedSilo.Json(HttpStatusCode.OK, Section(1, 1, [10])));
        var titles = await ShownAsync(Row);

        await titles.ShowAsync(Row, isReturn: true);

        Assert.Equal(["Movie 10"], Titles(titles));
    }

    [Fact]
    public async Task ARowWithNothing_SaysSo()
    {
        _harness.Answer(SectionPath, Section(1, 1, []));

        var titles = await ShownAsync(Row);

        Assert.True(titles.IsEmpty);
        Assert.Equal("Nothing here right now.", titles.Message);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(422)]
    public async Task ARowTheServerDoesNotKnow_DoesNotExist(int status)
    {
        _harness.Answer(SectionPath, ScriptedSilo.Problem(status, "not_found"));

        var titles = await ShownAsync(Row);

        Assert.True(titles.IsNotFound);
        Assert.False(titles.HasError);
        Assert.Equal("Not found", titles.Title);
        Assert.Equal("This Discover row doesn’t exist.", titles.Message);
    }

    [Fact]
    public async Task AKeyThatIsNoRow_IsNotAskedFor()
    {
        var titles = await ShownAsync(new RequestTitlesRouteParameter("../admin", ""));

        Assert.True(titles.IsNotFound);
        Assert.Empty(_harness.Silo.Sent);
    }

    [Fact]
    public async Task TmdbFailing_CanBeTriedAgain()
    {
        _harness.Answer(SectionPath, ScriptedSilo.Problem(502, "bad_gateway"), ScriptedSilo.Json(HttpStatusCode.OK, Section(1, 1, [10])));
        var titles = await ShownAsync(Row);
        Assert.True(titles.HasError);
        Assert.Equal("TMDB couldn’t be reached. Try again in a moment.", titles.Message);

        await titles.RetryCommand.ExecuteAsync(null);

        Assert.Equal(["Movie 10"], Titles(titles));
    }

    [Fact]
    public async Task ALaterPageThatFails_KeepsWhatLoaded_AndIsTriedAgainOnRequest()
    {
        _harness.Answer(SectionPath, ScriptedSilo.Json(HttpStatusCode.OK, Section(1, 3, [10])), ScriptedSilo.NetworkError(),
            ScriptedSilo.Json(HttpStatusCode.OK, Section(2, 3, [11])));
        var titles = await ShownAsync(Row);

        await titles.LoadMoreCommand.ExecuteAsync(null);
        Assert.True(titles.HasMoreError);
        Assert.Equal(["Movie 10"], Titles(titles));
        // Scrolling no longer reads it.
        Assert.False(titles.LoadMoreCommand.CanExecute(null));

        await titles.TryAgainCommand.ExecuteAsync(null);
        Assert.False(titles.HasMoreError);
        Assert.Equal(["Movie 10", "Movie 11"], Titles(titles));
        Assert.Equal(["page=1", "page=2", "page=2"], _harness.Queries(SectionPath));
    }

    [Fact]
    public async Task ACard_OpensInTheLibrary_OrOnItsOwnPage()
    {
        _harness.Answer(SectionPath, Section(1, 1, [10, 99]));
        var titles = await ShownAsync(Row);

        titles.OpenCardCommand.Execute(titles.Cards[1]);
        titles.OpenCardCommand.Execute(titles.Cards[0]);

        Assert.Equal(
        [
            (AppRoute.Item, (object?)"movie:m99"),
            (AppRoute.Title, new TitleRouteParameter("movie", 10, "Movie 10")),
        ], _harness.Navigation.Navigations.Select(n => (n.Route, n.Parameter)).ToList<(AppRoute, object?)>());
    }

    [Fact]
    public async Task Back_KeepsThePagesRead()
    {
        _harness.Answer(SectionPath, Section(1, 3, [10]), Section(2, 3, [11]));
        var titles = await ShownAsync(Row);
        await titles.LoadMoreCommand.ExecuteAsync(null);

        await titles.ShowAsync(Row, isReturn: true);

        Assert.Equal(2, _harness.Count(SectionPath));
        Assert.Equal(["Movie 10", "Movie 11"], Titles(titles));
    }

    [Fact]
    public async Task AStudio_ShowsItsLogoAndKind_SortedByPopularity()
    {
        _harness.Answer(StudioPath, Browse(1, 3, [10]));

        var titles = await ShownAsync(Studio);

        Assert.Equal("Marvel Studios", titles.Title);
        Assert.Equal("Studio", titles.Subtitle);
        Assert.Equal(new Uri(Logo), titles.Logo);
        Assert.True(titles.HasHeaderTile);
        Assert.True(titles.HasChoices);
        Assert.False(titles.IsGenre);
        Assert.Equal(["Popularity", "Rating", "Release date"], titles.SortChoices.Select(c => c.Label));
        Assert.Equal("Popularity", titles.SelectedSort.Label);
        Assert.Equal(["sort=popularity&page=1"], _harness.Queries(StudioPath));
    }

    [Fact]
    public async Task AnotherSort_ListsTheTitlesAgain()
    {
        _harness.Answer(StudioPath, Browse(1, 3, [10]), Browse(1, 3, [20]));
        var titles = await ShownAsync(Studio);

        titles.SelectedSort = titles.SortChoices[1];
        await WaitUntilAsync(() => titles.IsReady && titles.Cards.Count == 1 && titles.Cards[0].Title == "Movie 20");

        Assert.Equal(["sort=popularity&page=1", "sort=vote_average&page=1"], _harness.Queries(StudioPath));
    }

    [Fact]
    public async Task AnotherSort_KeepsTheNameAndLogo_WhileItLoads()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(StudioPath, ScriptedSilo.Json(HttpStatusCode.OK, Browse(1, 3, [10])), LibraryHarness.Held(release, Browse(1, 3, [20])));
        var titles = await ShownAsync(Studio);

        titles.SelectedSort = titles.SortChoices[2];

        Assert.True(titles.IsLoading);
        Assert.Equal("Marvel Studios", titles.Title);
        Assert.Equal("Studio", titles.Subtitle);
        Assert.NotNull(titles.Logo);
        release.SetResult();
        await WaitUntilAsync(() => titles.IsReady);
    }

    [Fact]
    public async Task AnotherSort_RecoversFromAFailure()
    {
        _harness.Answer(StudioPath, ScriptedSilo.Problem(502, "bad_gateway"), ScriptedSilo.Json(HttpStatusCode.OK, Browse(1, 1, [10])));
        var titles = await ShownAsync(Studio);
        Assert.True(titles.HasError);

        titles.SelectedSort = titles.SortChoices[1];
        await WaitUntilAsync(() => titles.IsReady);

        Assert.Equal(["Movie 10"], Titles(titles));
        Assert.Equal("Studio", titles.Subtitle);
    }

    [Fact]
    public async Task ASortChosenWhileAPageLoads_OnlyShowsItsOwnTitles()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(StudioPath, ScriptedSilo.Json(HttpStatusCode.OK, Browse(1, 3, [10])), LibraryHarness.Held(release, Browse(2, 3, [11])),
            ScriptedSilo.Json(HttpStatusCode.OK, Browse(1, 3, [20])));
        var titles = await ShownAsync(Studio);

        var more = titles.LoadMoreCommand.ExecuteAsync(null);
        titles.SelectedSort = titles.SortChoices[2];
        await WaitUntilAsync(() => titles.Cards.Count == 1 && titles.Cards[0].Title == "Movie 20");
        release.SetResult();
        await more;

        Assert.Equal(["Movie 20"], Titles(titles));
        Assert.False(titles.IsLoadingMore);
    }

    [Fact]
    public async Task AGenre_ShowsItsMoviesOrItsSeries()
    {
        _harness.Answer(GenrePath, Browse(1, 1, [10], name: "Action", logo: ""), Browse(1, 1, [30], name: "Action", logo: ""));
        var titles = await ShownAsync(Genre);
        Assert.True(titles.IsGenre);
        Assert.Null(titles.Logo);
        Assert.False(titles.HasLogo);
        Assert.Equal("Action", titles.TileText);
        Assert.Equal(["Movies", "Series"], titles.MediaChoices.Select(c => c.Label));

        titles.SelectedMedia = titles.MediaChoices[1];
        await WaitUntilAsync(() => titles.Cards.Count == 1 && titles.Cards[0].Title == "Movie 30");

        Assert.Equal(["sort=popularity&page=1&media_type=movie", "sort=popularity&page=1&media_type=series"], _harness.Queries(GenrePath));
    }

    [Fact]
    public async Task AGenre_CanOpenOnItsSeries()
    {
        _harness.Answer(GenrePath, Browse(1, 1, [30], name: "Action", logo: ""));

        var titles = await ShownAsync(Genre with { MediaType = "series" });

        Assert.Equal("Series", titles.SelectedMedia.Label);
        Assert.Equal(["sort=popularity&page=1&media_type=series"], _harness.Queries(GenrePath));
    }

    [Fact]
    public async Task AStudioThatDoesNotExist_SaysSoInItsHeading()
    {
        _harness.Answer(StudioPath, ScriptedSilo.Problem(404, "not_found"));

        var titles = await ShownAsync(Studio);

        Assert.True(titles.IsNotFound);
        Assert.Equal("Studio not found.", titles.Title);
        Assert.Equal("", titles.Message);
        Assert.False(titles.HasHeaderTile);
        Assert.False(titles.HasChoices);
    }

    [Fact]
    public async Task ABrowsePageThatFails_SuggestsAnotherSort()
    {
        _harness.Answer(StudioPath, ScriptedSilo.Problem(502, "bad_gateway"));

        var titles = await ShownAsync(Studio);

        Assert.True(titles.HasError);
        Assert.True(titles.HasChoices);
        Assert.Equal("Could not load this browse page. Try a different sort or media type.", titles.Message);
    }

    [Fact]
    public async Task ABrowsePageWithNothing_SuggestsAnotherSort()
    {
        _harness.Answer(StudioPath, Browse(1, 1, []));

        var titles = await ShownAsync(Studio);

        Assert.True(titles.IsEmpty);
        Assert.Equal("Nothing matched. Try a different sort.", titles.Message);
    }

    [Fact]
    public async Task ALogoOffTmdb_IsNotShown()
    {
        _harness.Answer(StudioPath, Browse(1, 1, [10], logo: "https://evil.example/t/p/w780/marvel.png"));

        var titles = await ShownAsync(Studio);

        Assert.Null(titles.Logo);
        Assert.Equal("Marvel Studios", titles.TileText);
    }

    [Theory]
    [InlineData("https://image.tmdb.org/t/p/w780_filter(duotone,ffffff,bababa)/abc.png", true)]
    [InlineData("https://image.tmdb.org/t/p/original/abc.png", true)]
    [InlineData("https://image.tmdb.org/t/p/original/abc.svg", false)]
    [InlineData("http://image.tmdb.org/t/p/w780/abc.png", false)]
    [InlineData("https://image.tmdb.org.evil.example/t/p/w780/abc.png", false)]
    [InlineData("https://user@image.tmdb.org/t/p/w780/abc.png", false)]
    [InlineData("https://image.tmdb.org:8443/t/p/w780/abc.png", false)]
    [InlineData("https://image.tmdb.org/t/p/w780/abc.png?x=1", false)]
    [InlineData("https://image.tmdb.org/t/p/w780/../../abc.png", false)]
    [InlineData("https://image.tmdb.org/t/p/w780_filter(<script>)/abc.png", false)]
    [InlineData("https://image.tmdb.org/other/abc.png", false)]
    [InlineData("/t/p/w780/abc.png", false)]
    [InlineData("", false)]
    public void ALogo_StaysOnTmdbsImageServer(string url, bool shown) => Assert.Equal(shown, TmdbImages.Logo(url) is not null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
}
