using System.Net;
using Granary.Core.Api;
using Granary.Core.ViewModels;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

public sealed class LibraryBrowseTests
{
    private const string CatalogPath = LibraryHarness.CatalogPath;
    private const string Images = LibraryHarness.ImageQuery;
    private const string FirstQuery = "library_id=1&type=movie&sort=title&limit=60&" + Images;

    private readonly LibraryHarness _harness = new();

    [Fact]
    public async Task LibraryTab_ListsTitlesInTitleOrderWithTheImageSizes()
    {
        var library = await _harness.OpenLibraryTabAsync();

        Assert.Equal(FirstQuery, Assert.Single(_harness.Queries(CatalogPath)));
        Assert.Equal("library_id=1&type=movie&skip_technical=true", Assert.Single(_harness.Queries("api/v2/catalog/filters")));
        var browse = library.Browse;
        Assert.Equal(new[] { "Alien", "Heat" }, browse.Grid.Cards.Select(c => c.Title));
        Assert.Equal("1,234 titles", browse.Grid.TotalText);
        Assert.Equal("Title", browse.SortLabel);
        Assert.Equal("Ascending", browse.OrderLabel);
        Assert.True(browse.IsLetterBarVisible);
        Assert.True(browse.Grid.HasMore);
    }

    [Fact]
    public async Task SeriesLibrary_ListsSeries()
    {
        _harness.Answer("api/v2/library/2/sections", LibraryPayloads.Sections);
        _harness.Answer("api/v2/library/2/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);

        await _harness.OpenLibraryTabAsync("2");

        Assert.StartsWith("library_id=2&type=series&sort=title&", Assert.Single(_harness.Queries(CatalogPath)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paging_FollowsTheCursorUntilTheLastPage()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var grid = library.Browse.Grid;

        await grid.LoadMoreCommand.ExecuteAsync(null);
        await grid.LoadMoreCommand.ExecuteAsync(null);

        var queries = _harness.Queries(CatalogPath);
        Assert.Equal(2, queries.Count);
        Assert.Equal($"library_id=1&type=movie&sort=title&limit=60&cursor=c2&{Images}", queries[1]);
        Assert.Equal(new[] { "Alien", "Heat", "Se7en" }, grid.Cards.Select(c => c.Title));
        Assert.False(grid.HasMore);
        Assert.False(grid.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadMore_WhileAPageLoads_AsksOnlyOnce()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(CatalogPath, ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.FirstPage), LibraryHarness.Held(release, LibraryPayloads.LastPage));
        var library = await _harness.OpenLibraryTabAsync();
        var grid = library.Browse.Grid;

        var first = grid.LoadMoreCommand.ExecuteAsync(null);
        var second = grid.LoadMoreCommand.ExecuteAsync(null);
        grid.LoadMoreCommand.Execute(null);
        Assert.True(grid.IsLoadingMore);
        Assert.False(grid.LoadMoreCommand.CanExecute(null));
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, _harness.Count(CatalogPath));
        Assert.Equal(3, grid.Cards.Count);
        Assert.False(grid.IsLoadingMore);
    }

    [Fact]
    public async Task Sort_StartsOverInTheFieldsOwnOrder()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;
        var dateAdded = browse.SortOptions.Single(o => o.Label == "Date added");
        int restarts = 0;
        browse.Grid.Restarted += (_, _) => restarts++;

        await browse.SetSortCommand.ExecuteAsync(dateAdded);

        Assert.True(browse.IsDescending);
        Assert.Equal("Descending", browse.OrderLabel);
        Assert.False(browse.IsLetterBarVisible);
        Assert.Equal($"library_id=1&type=movie&sort=-added_at&limit=60&{Images}", _harness.Queries(CatalogPath)[^1]);
        Assert.Equal(new[] { "Se7en" }, browse.Grid.Cards.Select(c => c.Title));
        Assert.Equal(1, restarts);

        await browse.SortAscendingCommand.ExecuteAsync(null);

        Assert.Equal($"library_id=1&type=movie&sort=added_at&limit=60&{Images}", _harness.Queries(CatalogPath)[^1]);
    }

    [Theory]
    [InlineData("Release date", "-release_date")]
    [InlineData("Year", "-year")]
    [InlineData("IMDb rating", "-rating_imdb")]
    [InlineData("Runtime", "-runtime")]
    public async Task Sort_UsesTheServersFieldNames(string label, string sort)
    {
        var library = await _harness.OpenLibraryTabAsync();

        await library.Browse.SetSortCommand.ExecuteAsync(library.Browse.SortOptions.Single(o => o.Label == label));

        Assert.Contains($"&sort={sort}&", _harness.Queries(CatalogPath)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Order_TogglesTitleSort()
    {
        var library = await _harness.OpenLibraryTabAsync();

        await library.Browse.ToggleOrderCommand.ExecuteAsync(null);
        await library.Browse.SortDescendingCommand.ExecuteAsync(null);

        Assert.Equal(2, _harness.Count(CatalogPath));
        Assert.Equal($"library_id=1&type=movie&sort=-title&limit=60&{Images}", _harness.Queries(CatalogPath)[^1]);
    }

    [Fact]
    public async Task Filters_NarrowByGenreRatingsAndYears()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;
        Assert.Equal(new[] { LibraryFiltersViewModel.AnyGenre, "Crime", "Drama" }, filters.GenreChoices);
        Assert.Equal(new[] { "PG-13", "R" }, filters.RatingOptions.Select(o => o.Value));

        filters.BeginEdit();
        filters.DraftGenreIndex = 2;
        foreach (var rating in filters.RatingOptions)
        {
            rating.IsSelected = true;
        }
        filters.DraftYearFrom = 1999;
        filters.DraftYearTo = 1990.4;
        await filters.ApplyCommand.ExecuteAsync(null);

        Assert.Equal($"library_id=1&type=movie&genre=Drama&year_min=1990&year_max=1999&content_rating=PG-13&content_rating=R&sort=title&limit=60&{Images}",
            _harness.Queries(CatalogPath)[^1]);
        Assert.Equal(4, filters.ActiveCount);
        Assert.True(filters.HasActiveFilters);
        Assert.True(library.Browse.IsNarrowed);
    }

    [Fact]
    public async Task Filters_OnlyAYearFrom_CountsOnce()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;

        filters.BeginEdit();
        filters.DraftYearFrom = 2000;
        await filters.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, filters.ActiveCount);
        Assert.Equal($"library_id=1&type=movie&year_min=2000&sort=title&limit=60&{Images}", _harness.Queries(CatalogPath)[^1]);
    }

    [Fact]
    public async Task Filters_Clear_ListsEverythingAgain()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;
        filters.BeginEdit();
        filters.DraftGenreIndex = 1;
        await filters.ApplyCommand.ExecuteAsync(null);

        await filters.ClearCommand.ExecuteAsync(null);

        Assert.Equal(0, filters.ActiveCount);
        Assert.Equal(0, filters.DraftGenreIndex);
        Assert.Equal(FirstQuery, _harness.Queries(CatalogPath)[^1]);
    }

    [Fact]
    public async Task Filters_ReopenedFlyout_ShowsTheAppliedOnesNotTheAbandonedDraft()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;
        filters.BeginEdit();
        filters.DraftGenreIndex = 1;
        filters.RatingOptions[1].IsSelected = true;
        await filters.ApplyCommand.ExecuteAsync(null);
        filters.BeginEdit();
        filters.DraftGenreIndex = 2;
        filters.DraftYearFrom = 1980;

        filters.BeginEdit();

        Assert.Equal(1, filters.DraftGenreIndex);
        Assert.True(double.IsNaN(filters.DraftYearFrom));
        Assert.True(filters.RatingOptions[1].IsSelected);
        Assert.False(filters.RatingOptions[0].IsSelected);
    }

    [Fact]
    public async Task FailedFilterOptions_LeaveTheYearRange()
    {
        _harness.Answer("api/v2/catalog/filters", ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable));

        var library = await _harness.OpenLibraryTabAsync();

        Assert.True(library.Browse.Filters.HasOptionsError);
        Assert.Equal(new[] { LibraryFiltersViewModel.AnyGenre }, library.Browse.Filters.GenreChoices);
        Assert.Equal(2, library.Browse.Grid.Cards.Count);
    }

    [Fact]
    public async Task LeavingWhileTheFilterOptionsLoad_EndsQuietly()
    {
        var release = new TaskCompletionSource();
        _harness.Answer("api/v2/catalog/filters", async () =>
        {
            // A handler that saw the cancellation.
            await release.Task;
            throw new TaskCanceledException();
        });
        _harness.Answer("api/v2/library/2/sections", LibraryPayloads.Sections);
        _harness.Answer("api/v2/library/2/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);
        var library = _harness.Create();
        await library.ActivateAsync("1");

        var tab = library.SelectTabCommand.ExecuteAsync(LibraryTab.Library);
        await library.ActivateAsync("2");
        release.SetResult();
        await tab;

        Assert.Equal("Serien", library.LibraryName);
        Assert.False(library.Browse.Filters.HasOptionsError);
        Assert.DoesNotContain(_harness.Logs.Lines, l => l.StartsWith("Warning LibraryViewModel: Could not load the filter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Letter_NarrowsByNamePrefixAndThePickedLetterAgainClearsIt()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;

        await browse.SelectLetterCommand.ExecuteAsync("b");

        Assert.Equal("B", browse.NamePrefix);
        Assert.Equal($"library_id=1&name_prefix=B&type=movie&sort=title&limit=60&{Images}", _harness.Queries(CatalogPath)[^1]);
        Assert.True(browse.IsNarrowed);

        await browse.SelectLetterCommand.ExecuteAsync("B");

        Assert.Null(browse.NamePrefix);
        Assert.Equal(FirstQuery, _harness.Queries(CatalogPath)[^1]);
    }

    [Theory]
    [InlineData("#")]
    [InlineData(null)]
    [InlineData("")]
    public async Task HashOrNoLetter_ShowsEveryTitle(string? letter)
    {
        var library = await _harness.OpenLibraryTabAsync();
        await library.Browse.SelectLetterCommand.ExecuteAsync("M");

        await library.Browse.SelectLetterCommand.ExecuteAsync(letter);

        Assert.Null(library.Browse.NamePrefix);
        Assert.DoesNotContain("name_prefix", _harness.Queries(CatalogPath)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Letter_IsIgnoredOutsideTitleOrderAndClearedWhenTheOrderChanges()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;
        await browse.SelectLetterCommand.ExecuteAsync("K");

        await browse.SetSortCommand.ExecuteAsync(browse.SortOptions.Single(o => o.Label == "Year"));
        int requests = _harness.Count(CatalogPath);
        await browse.SelectLetterCommand.ExecuteAsync("C");

        Assert.Null(browse.NamePrefix);
        Assert.False(browse.IsLetterBarVisible);
        Assert.Equal(requests, _harness.Count(CatalogPath));
        Assert.DoesNotContain("name_prefix", _harness.Queries(CatalogPath)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Letter_OtherThanALetter_IsIgnored()
    {
        var library = await _harness.OpenLibraryTabAsync();

        await library.Browse.SelectLetterCommand.ExecuteAsync("Ab");
        await library.Browse.SelectLetterCommand.ExecuteAsync("7");

        Assert.Equal(1, _harness.Count(CatalogPath));
    }

    [Fact]
    public async Task EmptyResultWithFilters_OffersToClearThem()
    {
        _harness.Answer(CatalogPath, LibraryPayloads.FirstPage, LibraryPayloads.EmptyPage, LibraryPayloads.FirstPage);
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;
        await browse.SelectLetterCommand.ExecuteAsync("Q");
        browse.Filters.BeginEdit();
        browse.Filters.DraftGenreIndex = 1;
        _harness.Answer(CatalogPath, LibraryPayloads.EmptyPage, LibraryPayloads.FirstPage);
        await browse.Filters.ApplyCommand.ExecuteAsync(null);

        Assert.True(browse.Grid.IsEmpty);
        Assert.True(browse.IsNarrowed);
        Assert.Equal("0 titles", browse.Grid.TotalText);

        await browse.ClearFiltersCommand.ExecuteAsync(null);

        Assert.False(browse.IsNarrowed);
        Assert.Equal(0, browse.Filters.ActiveCount);
        Assert.Equal(FirstQuery, _harness.Queries(CatalogPath)[^1]);
        Assert.True(browse.Grid.IsReady);
    }

    [Fact]
    public async Task EmptyLibrary_IsNotNarrowed()
    {
        _harness.Answer(CatalogPath, LibraryPayloads.EmptyPage);

        var library = await _harness.OpenLibraryTabAsync();

        Assert.True(library.Browse.Grid.IsEmpty);
        Assert.False(library.Browse.IsNarrowed);
    }

    [Fact]
    public async Task FailedFirstPage_ShowsAFriendlyTextAndRetryRecovers()
    {
        _harness.Answer(CatalogPath, ScriptedSilo.Problem(429, ProblemCodes.RateLimited), ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.FirstPage));
        var library = await _harness.OpenLibraryTabAsync();
        var grid = library.Browse.Grid;
        Assert.True(grid.HasError);
        Assert.Equal(LibraryErrors.Busy, grid.ErrorMessage);
        Assert.False(grid.LoadMoreCommand.CanExecute(null));

        await grid.RetryCommand.ExecuteAsync(null);

        Assert.True(grid.IsReady);
        Assert.Equal(2, grid.Cards.Count);
        Assert.Equal(FirstQuery, _harness.Queries(CatalogPath)[^1]);
    }

    [Fact]
    public async Task FailedLaterPage_KeepsTheCardsUntilRetried()
    {
        _harness.Answer(CatalogPath, ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.FirstPage), ScriptedSilo.NetworkError(),
            ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.LastPage));
        var library = await _harness.OpenLibraryTabAsync();
        var grid = library.Browse.Grid;

        await grid.LoadMoreCommand.ExecuteAsync(null);

        Assert.True(grid.HasLoadMoreError);
        Assert.True(grid.IsReady);
        Assert.Equal(2, grid.Cards.Count);
        Assert.False(grid.LoadMoreCommand.CanExecute(null));

        await grid.RetryLoadMoreCommand.ExecuteAsync(null);

        Assert.False(grid.HasLoadMoreError);
        Assert.Equal(3, grid.Cards.Count);
    }

    [Fact]
    public async Task RefusedCursor_StartsTheListingOver()
    {
        _harness.Answer(CatalogPath, ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.FirstPage), ScriptedSilo.Problem(400, ProblemCodes.InvalidCursor),
            ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.FirstPage));
        var library = await _harness.OpenLibraryTabAsync();

        await library.Browse.Grid.LoadMoreCommand.ExecuteAsync(null);

        var queries = _harness.Queries(CatalogPath);
        Assert.Equal(3, queries.Count);
        Assert.Equal(FirstQuery, queries[2]);
        Assert.Equal(2, library.Browse.Grid.Cards.Count);
        Assert.False(library.Browse.Grid.HasLoadMoreError);
    }

    [Fact]
    public async Task FailedPageOfAReplacedListing_LeavesTheNewOneLoadable()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(CatalogPath, ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.FirstPage), async () =>
        {
            await release.Task;
            throw new HttpRequestException("connection reset");
        }, ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.FirstPage));
        var library = await _harness.OpenLibraryTabAsync();
        var grid = library.Browse.Grid;

        var replaced = grid.LoadMoreCommand.ExecuteAsync(null);
        await library.Browse.ToggleOrderCommand.ExecuteAsync(null);
        release.SetResult();
        await replaced;

        Assert.True(grid.IsReady);
        Assert.False(grid.HasLoadMoreError);
        Assert.False(grid.IsLoadingMore);
        Assert.True(grid.LoadMoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChangeDuringALoad_ShowsOnlyTheNewestListing()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(CatalogPath, ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.FirstPage), LibraryHarness.Held(release, LibraryPayloads.FirstPage),
            ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.LastPage));
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;

        var stale = browse.SelectLetterCommand.ExecuteAsync("A");
        await browse.SelectLetterCommand.ExecuteAsync("H");
        release.SetResult();
        await stale;

        Assert.Equal("H", browse.NamePrefix);
        Assert.Equal(new[] { "Se7en" }, browse.Grid.Cards.Select(c => c.Title));
    }
}
