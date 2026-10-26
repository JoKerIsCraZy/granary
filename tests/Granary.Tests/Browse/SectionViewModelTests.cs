using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

public sealed class SectionViewModelTests
{
    private const string CatalogPath = "api/v2/catalog";

    private static readonly SectionRouteParameter HomeRow = new("default-recently-added-tv", "Recently Added TV", SectionType: "recently_added");

    private readonly BrowseFakeSilo _silo = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly RecordingNavigation _navigation = new();

    private SectionViewModel Create() => new(_session, _silo.Catalog, _silo.Images, _navigation, _silo.Logs.For<SectionViewModel>());

    /// <summary>Answers each cursor with its page: null is page one.</summary>
    private void Pages(params (string? Cursor, string Page)[] pages) => _silo.Map(CatalogPath, request =>
    {
        string? cursor = new BrowseRequest(request.Method, request.RequestUri!, null).Query("cursor");
        return pages.FirstOrDefault(p => p.Cursor == cursor) is { Page: { } page }
            ? BrowseFakeSilo.Json(page)
            : BrowseFakeSilo.Problem(400, ProblemCodes.InvalidCursor);
    });

    [Fact]
    public async Task HomeRow_PagesWithItsCursorUntilTheLastPage()
    {
        Pages((null, Page("c1", Item("m1"), Item("m2"))), ("c1", Page(null, Item("m3"))));
        var section = Create();

        await section.OpenAsync(HomeRow);
        Assert.Equal(["m1", "m2"], section.Cards.Select(c => c.Id));
        await section.LoadMoreCommand.ExecuteAsync(null);
        await section.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(["m1", "m2", "m3"], section.Cards.Select(c => c.Id));
        var requests = _silo.To(CatalogPath);
        Assert.Equal(2, requests.Count);
        Assert.Equal("section", requests[0].Query("source"));
        Assert.Equal("home", requests[0].Query("scope"));
        Assert.Equal("default-recently-added-tv", requests[0].Query("section_id"));
        Assert.Equal("60", requests[0].Query("limit"));
        Assert.Null(requests[0].Query("sort"));
        Assert.Null(requests[0].Query("cursor"));
        Assert.Equal("c1", requests[1].Query("cursor"));
        Assert.Equal("342", requests[0].Query("poster_width"));
        Assert.Equal(requests[0].Query("poster_width"), requests[1].Query("poster_width"));
        Assert.Equal("Recently Added TV", section.Title);
        Assert.Equal(MediaCardShape.Poster, section.Cards[0].Shape);
    }

    [Fact]
    public async Task LibraryRow_IsListedInItsLibrary()
    {
        Pages((null, Page(null, Item("m1"))));
        var section = Create();

        await section.OpenAsync(new SectionRouteParameter("s9", "Comedy", "12"));

        var request = Assert.Single(_silo.To(CatalogPath));
        Assert.Equal("library", request.Query("scope"));
        Assert.Equal("12", request.Query("library_id"));
        Assert.Equal("s9", request.Query("section_id"));
    }

    [Fact]
    public async Task ContinueWatching_ListsItsVideosAsPosters_AsTheWebCatalog()
    {
        Pages((null, Page(null, Item("episode-1", "episode", "series-1"), Item("book-1", "audiobook"))));
        var section = Create();

        await section.OpenAsync(new SectionRouteParameter("cw", "Continue Watching", SectionType: "continue_watching"));

        var card = Assert.Single(section.Cards);
        Assert.Equal(MediaCardShape.Poster, card.Shape);
        Assert.Equal(("Series series-1", "Title episode-1", "S01E02"), (card.Title, card.EpisodeTitle, card.Subtitle));
    }

    [Fact]
    public async Task StaleCursor_RestartsFromPageOne()
    {
        Pages((null, Page("c1", Item("m1"), Item("m2"))));
        var section = Create();
        await section.OpenAsync(HomeRow);

        await section.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal([null, "c1", null], _silo.To(CatalogPath).Select(r => r.Query("cursor")));
        Assert.Equal(["m1", "m2"], section.Cards.Select(c => c.Id));
        Assert.False(section.HasError);
    }

    [Fact]
    public async Task FailedFirstPage_ShowsAFriendlyErrorAndRetryRecovers()
    {
        _silo.Map(CatalogPath, _ => BrowseFakeSilo.Problem(503, ProblemCodes.DependencyUnavailable));
        var section = Create();

        await section.OpenAsync(HomeRow);

        Assert.True(section.HasError);
        Assert.Equal("The server is busy. Try again in a moment.", section.StatusText);

        Pages((null, Page(null, Item("m1"))));
        await section.RetryCommand.ExecuteAsync(null);

        Assert.False(section.HasError);
        Assert.Equal(["m1"], section.Cards.Select(c => c.Id));
        Assert.Equal("", section.StatusText);
    }

    [Fact]
    public async Task RemovedRow_SaysItIsGone()
    {
        _silo.Map(CatalogPath, _ => BrowseFakeSilo.Problem(404, ProblemCodes.NotFound));
        var section = Create();

        await section.OpenAsync(HomeRow);

        Assert.Equal("This row is no longer available.", section.StatusText);
    }

    [Fact]
    public async Task FailedLaterPage_WaitsForRetryInsteadOfAskingWhileScrolling()
    {
        int laterPages = 0;
        _silo.Map(CatalogPath, request =>
        {
            string? cursor = new BrowseRequest(request.Method, request.RequestUri!, null).Query("cursor");
            if (cursor is null)
            {
                return BrowseFakeSilo.Json(Page("c1", Item("m1")));
            }
            return ++laterPages == 1
                ? BrowseFakeSilo.Problem(500, "internal_error")
                : BrowseFakeSilo.Json(Page(null, Item("m2")));
        });
        var section = Create();
        await section.OpenAsync(HomeRow);

        await section.LoadMoreCommand.ExecuteAsync(null);
        Assert.True(section.HasError);
        Assert.Equal("This row could not be loaded right now.", section.StatusText);
        await section.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(1, laterPages);

        await section.RetryCommand.ExecuteAsync(null);

        Assert.Equal(["m1", "m2"], section.Cards.Select(c => c.Id));
        Assert.False(section.HasError);
    }

    [Fact]
    public async Task RowWithoutAnId_SaysItIsGoneWithoutAsking()
    {
        var section = Create();

        await section.OpenAsync(new SectionRouteParameter("", "Broken"));

        Assert.True(section.HasError);
        Assert.Equal("This row is no longer available.", section.StatusText);
        Assert.Empty(_silo.To(CatalogPath));
    }

    [Fact]
    public async Task EmptyRow_ShowsNothingHereYet()
    {
        Pages((null, Page(null)));
        var section = Create();

        await section.OpenAsync(HomeRow);

        Assert.True(section.IsEmpty);
        Assert.Equal("Nothing here yet", section.StatusText);
    }

    [Fact]
    public async Task ReturningToTheSameRow_KeepsItsPages()
    {
        Pages((null, Page(null, Item("m1"))));
        var section = Create();
        await section.OpenAsync(HomeRow);

        await section.OpenAsync(HomeRow);
        Assert.Single(_silo.To(CatalogPath));

        await section.OpenAsync(HomeRow, reload: true);
        Assert.Equal(2, _silo.To(CatalogPath).Count);
    }

    [Fact]
    public async Task Card_OpensTheItem()
    {
        Pages((null, Page(null, Item("movie-5"))));
        var section = Create();
        await section.OpenAsync(HomeRow);

        section.OpenCardCommand.Execute(section.Cards[0]);

        Assert.Equal((AppRoute.Item, (object?)"movie-5"), Assert.Single(_navigation.Navigations));
    }
}
