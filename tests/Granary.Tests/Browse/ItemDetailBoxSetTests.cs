using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Profiles;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

/// <summary>A movie's box set row, as the web's <c>MovieBoxSetRow</c> shows it and the apps' rows behave.</summary>
public sealed class ItemDetailBoxSetTests
{
    private readonly ItemDetailFixture _fixture = new();

    public ItemDetailBoxSetTests()
    {
        _fixture.BoxSets = new BoxSetsApi(_fixture.Silo.Http);
        _fixture.BoxSetLibraries = new BoxSetLibraries(new CatalogApi(_fixture.Silo.Http), new Fakes.ManualTimeProvider(),
            _fixture.Logs.For<BoxSetLibraries>());
        _fixture.Get(LibrariesPath, Ok(BoxSetPayloads.Libraries(boxSets: true)));
    }

    private const string LibrariesPath = "api/v2/user/libraries";

    private static string BoxSetPath(string id) => $"api/v2/catalog/items/{id}/box-set";

    [Fact]
    public async Task NoLibraryWithBoxSetsOn_DoesNotAsk_AsTheWebsUseItemBoxSet()
    {
        _fixture.Movie().Get(LibrariesPath, Ok(BoxSetPayloads.Libraries(boxSets: false)));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, BoxSetPath(MovieId)));
        Assert.False(detail.HasBoxSet);
        Assert.True(detail.HasSimilar);
    }

    [Fact]
    public async Task UnreadableLibraries_DoNotAsk_AndShowNoError()
    {
        _fixture.Movie().Get(LibrariesPath, Busy());

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, BoxSetPath(MovieId)));
        Assert.Equal(ItemDetailState.Ready, detail.State);
        Assert.False(detail.HasBoxSet);
    }

    [Fact]
    public async Task TheLibraries_AreReadOnceForEveryMoviesPage()
    {
        _fixture.Movie().Get(BoxSetPath(MovieId), Ok(BoxSetPayloads.ItemBoxSet));

        await _fixture.LoadAsync(MovieId);
        var again = await _fixture.LoadAsync(MovieId);

        Assert.True(again.HasBoxSet);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, LibrariesPath));
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, BoxSetPath(MovieId)).Count);
    }

    [Fact]
    public async Task AMovieInABoxSet_ShowsTheSetsFilmsInReleaseOrder_ThisFilmMarked()
    {
        _fixture.Movie().Get(BoxSetPath(MovieId), Ok(BoxSetPayloads.ItemBoxSet));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.True(detail.HasBoxSet);
        Assert.Equal("Masterplan Box Set", detail.BoxSetTitle);
        Assert.Equal([BoxSetPayloads.FirstId, MovieId, BoxSetPayloads.LastId], detail.BoxSetFilms.Select(c => c.Id));
        var current = Assert.Single(detail.BoxSetFilms, c => c.IsCurrent);
        Assert.Equal(MovieId, current.Id);
        Assert.Equal(CurrentMark.ThisFilm, current.CurrentMark);
        Assert.Equal("Masterplan, 2026, this film", current.AccessibleName);
        // The other films are cards as any row's: their year, their badges, their progress.
        Assert.Equal("Groundwork, 2019", detail.BoxSetFilms[0].AccessibleName);
        Assert.Equal("2160p", detail.BoxSetFilms[2].Overlay?.Resolution);
        Assert.NotNull(current.Progress);
    }

    [Fact]
    public async Task TheFilmsOwnCard_OpensNothing_AnotherFilmOpensItsPage()
    {
        _fixture.Movie().Get(BoxSetPath(MovieId), Ok(BoxSetPayloads.ItemBoxSet));
        var detail = await _fixture.LoadAsync(MovieId);

        detail.OpenCardCommand.Execute(detail.BoxSetFilms[1]);
        detail.OpenCardCommand.Execute(detail.BoxSetFilms[2]);

        Assert.Equal((AppRoute.Item, (object?)BoxSetPayloads.LastId), Assert.Single(_fixture.Navigation.Navigations));
    }

    [Fact]
    public async Task ExploreAll_OpensTheBoxSetsPage()
    {
        _fixture.Movie().Get(BoxSetPath(MovieId), Ok(BoxSetPayloads.ItemBoxSet));
        var detail = await _fixture.LoadAsync(MovieId);

        detail.OpenBoxSetCommand.Execute(null);

        Assert.Equal((AppRoute.BoxSet, (object?)new BoxSetRouteParameter("1241", "3", "Masterplan Box Set")),
            Assert.Single(_fixture.Navigation.Navigations));
    }

    [Fact]
    public async Task AMovieInNoBoxSet_ShowsNoRow()
    {
        _fixture.Movie().Get(BoxSetPath(MovieId), Ok(BoxSetPayloads.NoBoxSet));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.False(detail.HasBoxSet);
        Assert.Empty(detail.BoxSetFilms);
        Assert.Equal("", detail.BoxSetTitle);
        detail.OpenBoxSetCommand.Execute(null);
        Assert.Empty(_fixture.Navigation.Navigations);
    }

    [Fact]
    public async Task AServerWithoutBoxSets_ShowsNoRowAndNoError()
    {
        _fixture.Movie().Get(BoxSetPath(MovieId), ScriptedSilo.Problem(404, "not_found"));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.Equal(ItemDetailState.Ready, detail.State);
        Assert.False(detail.HasBoxSet);
        Assert.False(detail.HasActionError);
        Assert.True(detail.HasSimilar);
        // Expected of an older server: nothing to warn about.
        Assert.DoesNotContain("box set", _fixture.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRead_OnlyLeavesTheRowOut()
    {
        _fixture.Movie().Get(BoxSetPath(MovieId), Busy());

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.Equal(ItemDetailState.Ready, detail.State);
        Assert.False(detail.HasBoxSet);
        Assert.True(detail.HasSimilar);
    }

    [Fact]
    public async Task ABoxSetWithoutFilms_ShowsNoRow()
    {
        _fixture.Movie().Get(BoxSetPath(MovieId), Ok("""
            { "box_set": { "id": "1241", "library_id": "3", "title": "Masterplan Box Set", "movie_count": 0, "played_count": 0, "items": [] } }
            """));

        var detail = await _fixture.LoadAsync(MovieId);

        Assert.False(detail.HasBoxSet);
    }

    [Fact]
    public async Task OnlyAMovieAsks()
    {
        _fixture.Series();

        var detail = await _fixture.LoadAsync(SeriesId);

        Assert.False(detail.HasBoxSet);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, BoxSetPath(SeriesId)));
    }

    [Fact]
    public async Task AnotherTitle_DropsTheLastOnesRow()
    {
        _fixture.Movie().Get(BoxSetPath(MovieId), Ok(BoxSetPayloads.ItemBoxSet)).Series();
        var detail = _fixture.Create();
        await detail.LoadCommand.ExecuteAsync(MovieId);
        Assert.True(detail.HasBoxSet);

        await detail.LoadCommand.ExecuteAsync(SeriesId);

        Assert.False(detail.HasBoxSet);
        Assert.Equal("", detail.BoxSetTitle);
    }
}
