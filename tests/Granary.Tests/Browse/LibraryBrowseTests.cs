using System.Net;
using Granary.Core.Api;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

public sealed class LibraryBrowseTests
{
    private const string CatalogPath = LibraryHarness.CatalogPath;
    private const string Images = LibraryHarness.ImageQuery;
    private const string FirstQuery = "library_id=1&type=movie&sort=title&limit=60&" + Images;
    private const string AllTitlesLetters = """{"library_id":"1","type":"movie","groups":[],"order":"asc"}""";

    private readonly LibraryHarness _harness = new();

    [Fact]
    public async Task LibraryTab_ListsTitlesInTitleOrderWithTheImageSizes()
    {
        var library = await _harness.OpenLibraryTabAsync();

        Assert.Equal(FirstQuery, Assert.Single(_harness.Queries(CatalogPath)));
        Assert.Equal("library_id=1&type=movie&skip_technical=true", Assert.Single(_harness.Queries("api/v2/catalog/filters")));
        var browse = library.Browse;
        Assert.Equal(new[] { "Alien", "Heat" }, browse.Grid.Cards.Select(c => c.Title));
        Assert.Equal("2 titles", browse.Grid.TotalText);
        Assert.Equal("Title", browse.SortLabel);
        Assert.Equal("Ascending", browse.OrderLabel);
        Assert.True(browse.IsLetterBarVisible);
    }

    [Fact]
    public async Task SeriesLibrary_ListsSeries()
    {
        _harness.Answer("api/v2/library/2/sections", LibraryPayloads.Sections);
        _harness.Answer("api/v2/library/2/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);

        await _harness.OpenLibraryTabAsync("2");

        Assert.StartsWith("library_id=2&type=series&sort=title&", Assert.Single(_harness.Queries(CatalogPath)), StringComparison.Ordinal);
        Assert.Equal("""{"library_id":"2","type":"series","groups":[],"order":"asc"}""", Assert.Single(_harness.LetterBodies()));
    }

    [Fact]
    public async Task Sort_StartsOverInTheFieldsOwnOrder()
    {
        _harness.Answer(CatalogPath, LibraryPayloads.OnlyPage, LibraryPayloads.LastPage);
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
        Assert.Equal("""{"library_id":"1","type":"movie","groups":[],"order":"desc"}""", _harness.LetterBodies()[^1]);
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
    public async Task Filters_AreTheLetterIndexsRuleGroups_AsTheServerMakesThem()
    {
        // GET /catalog turns genre and years into one group that must all match, and several ratings into one of
        // which any may: the index counts the same titles the pages list.
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;
        filters.BeginEdit();
        filters.DraftGenreIndex = 2;
        foreach (var rating in filters.RatingOptions)
        {
            rating.IsSelected = true;
        }
        filters.DraftYearFrom = 1990;
        filters.DraftYearTo = 1999;

        await filters.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("""
            {"library_id":"1","type":"movie","groups":[{"match":"all","rules":[{"field":"genre","op":"contains","value":"Drama"},{"field":"year","op":"between","value":[1990,1999]}]},{"match":"any","rules":[{"field":"content_rating","op":"is","value":"PG-13"},{"field":"content_rating","op":"is","value":"R"}]}],"order":"asc"}
            """, _harness.LetterBodies()[^1]);
    }

    [Fact]
    public async Task Filters_OnlyAYearFrom_CountsOnce()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;

        filters.BeginEdit();
        filters.DraftYearFrom = 2000;
        filters.RatingOptions[1].IsSelected = true;
        await filters.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(2, filters.ActiveCount);
        Assert.Equal($"library_id=1&type=movie&year_min=2000&content_rating=R&sort=title&limit=60&{Images}", _harness.Queries(CatalogPath)[^1]);
        Assert.Equal("""
            {"library_id":"1","type":"movie","groups":[{"match":"all","rules":[{"field":"year","op":"gte","value":2000}]},{"match":"all","rules":[{"field":"content_rating","op":"is","value":"R"}]}],"order":"asc"}
            """, _harness.LetterBodies()[^1]);
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
        Assert.Equal(AllTitlesLetters, _harness.LetterBodies()[^1]);
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
    public async Task LetterIndex_MarksTheLetterOfTheTopRow()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;
        Assert.Equal(AllTitlesLetters, Assert.Single(_harness.LetterBodies()));
        Assert.Equal(new[] { "A", "H" }, browse.Letters!.Letters.Select(l => l.Letter));
        Assert.Equal("A", browse.ActiveLetter);

        browse.ShowViewportCommand.Execute(new GridViewport(0, 1, TopRowEnd: 1, IsAtEnd: false));

        Assert.Equal("H", browse.ActiveLetter);
    }

    [Fact]
    public async Task Letter_ScrollsToItsFirstTitle_InsteadOfNarrowingTheListing()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;
        var jumps = new List<int>();
        browse.JumpRequested += (_, position) => jumps.Add(position);

        browse.JumpToLetterCommand.Execute("H");

        Assert.Equal([1], jumps);
        Assert.Equal("H", browse.ActiveLetter);
        Assert.Equal(1, _harness.Count(CatalogPath));
        Assert.False(browse.IsNarrowed);
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("#")]
    [InlineData(null)]
    public async Task Letter_WithoutTitles_DoesNothing(string? letter)
    {
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;
        var jumps = new List<int>();
        browse.JumpRequested += (_, position) => jumps.Add(position);

        browse.JumpToLetterCommand.Execute(letter);

        Assert.Empty(jumps);
        Assert.Equal("A", browse.ActiveLetter);
    }

    [Fact]
    public async Task JumpedLetter_StaysMarkedAtTheEnd_UntilTheGridScrollsAway()
    {
        // The last letters cannot scroll to the top: the top row there is still an earlier letter's.
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;
        browse.JumpToLetterCommand.Execute("A");

        browse.ShowViewportCommand.Execute(new GridViewport(0, 1, TopRowEnd: 1, IsAtEnd: true));

        Assert.Equal("A", browse.ActiveLetter);

        browse.ShowViewportCommand.Execute(new GridViewport(0, 1, TopRowEnd: 1, IsAtEnd: false));

        Assert.Equal("H", browse.ActiveLetter);
    }

    [Fact]
    public async Task OtherOrders_HaveNoLetterIndex()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;
        var jumps = new List<int>();
        browse.JumpRequested += (_, position) => jumps.Add(position);

        await browse.SetSortCommand.ExecuteAsync(browse.SortOptions.Single(o => o.Label == "Year"));
        browse.JumpToLetterCommand.Execute("H");

        Assert.Single(_harness.LetterBodies());
        Assert.Null(browse.Letters);
        Assert.Null(browse.ActiveLetter);
        Assert.False(browse.IsLetterBarVisible);
        Assert.Empty(jumps);
    }

    [Fact]
    public async Task FailedLetterIndex_SetsTheBarAside_AndLeavesTheGrid()
    {
        _harness.AnswerLetters(ScriptedSilo.Problem(404, ProblemCodes.NotFound));

        var library = await _harness.OpenLibraryTabAsync();

        Assert.True(library.Browse.HasLettersError);
        Assert.False(library.Browse.IsLetterBarVisible);
        Assert.True(library.Browse.Grid.IsReady);
        Assert.Contains(_harness.Logs.Lines, l => l.StartsWith("Warning LibraryViewModel: Could not load the letter index", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LetterIndexOfAReplacedListing_IsDropped()
    {
        var release = new TaskCompletionSource();
        const string Descending = """
            { "total": 2, "letters": [ { "letter": "H", "count": 1, "position": 0 }, { "letter": "A", "count": 1, "position": 1 } ],
              "runs": [ { "letter": "H", "position": 0 }, { "letter": "A", "position": 1 } ] }
            """;
        _harness.AnswerLetters(LibraryHarness.Held(release, LibraryPayloads.Letters), ScriptedSilo.Json(HttpStatusCode.OK, Descending));
        var library = _harness.Create();
        await library.ActivateAsync("1");
        var first = library.SelectTabCommand.ExecuteAsync(LibraryTab.Library);
        await Auth.AuthPipeline.Eventually(() => library.Browse.Grid.IsReady);

        await library.Browse.ToggleOrderCommand.ExecuteAsync(null);
        release.SetResult();
        await first;

        Assert.Equal("H", library.Browse.Letters!.Letters[0].Letter);
        Assert.Equal("H", library.Browse.ActiveLetter);
    }

    [Fact]
    public async Task ListingChangedUnderTheCursor_ReloadsTheLetterIndex()
    {
        // A full first page, then the next one refused: titles came or went, so the letters moved.
        string fullPage = """{ "window_cursor": "w1", "items": [""" + string.Join(", ", Enumerable.Range(0, 60).Select(i =>
            $$"""{ "content_id": "movie:m{{i}}", "type": "movie", "title": "M{{i}}", "genres": [], "keywords": [], "status": "matched" }"""))
            + """], "page": { "next_cursor": "c2", "has_more": true }, "total": 1234, "total_exact": true }""";
        _harness.Answer(CatalogPath, ScriptedSilo.Json(HttpStatusCode.OK, fullPage), ScriptedSilo.Problem(400, ProblemCodes.InvalidCursor),
            ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.OnlyPage));
        const string Moved = """
            { "total": 2, "letters": [ { "letter": "A", "count": 2, "position": 0 } ], "runs": [ { "letter": "A", "position": 0 } ] }
            """;
        _harness.AnswerLetters(ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.Letters), ScriptedSilo.Json(HttpStatusCode.OK, Moved));

        var library = await _harness.OpenLibraryTabAsync();
        await Auth.AuthPipeline.Eventually(() => library.Browse.Letters is { Letters.Count: 1 } && library.Browse.Grid.Cards.Count == 2);

        Assert.Equal(2, _harness.LetterBodies().Count);
        Assert.Equal("A", library.Browse.ActiveLetter);
    }

    [Fact]
    public async Task EmptyResultWithFilters_OffersToClearThem()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;
        browse.Filters.BeginEdit();
        browse.Filters.DraftGenreIndex = 1;
        _harness.Answer(CatalogPath, LibraryPayloads.EmptyPage, LibraryPayloads.OnlyPage);
        await browse.Filters.ApplyCommand.ExecuteAsync(null);

        Assert.True(browse.Grid.IsEmpty);
        Assert.True(browse.IsNarrowed);
        Assert.False(browse.IsLetterBarVisible);
        Assert.Equal("0 titles", browse.Grid.TotalText);

        await browse.ClearFiltersCommand.ExecuteAsync(null);

        Assert.False(browse.IsNarrowed);
        Assert.Equal(0, browse.Filters.ActiveCount);
        Assert.Equal(FirstQuery, _harness.Queries(CatalogPath)[^1]);
        Assert.True(browse.Grid.IsReady);
        Assert.True(browse.IsLetterBarVisible);
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
        _harness.Answer(CatalogPath, ScriptedSilo.Problem(429, ProblemCodes.RateLimited), ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.OnlyPage));
        var library = await _harness.OpenLibraryTabAsync();
        var grid = library.Browse.Grid;
        Assert.True(grid.HasError);
        Assert.Equal(LibraryErrors.Busy, grid.ErrorMessage);
        Assert.False(library.Browse.IsLetterBarVisible);

        await grid.RetryCommand.ExecuteAsync(null);

        Assert.True(grid.IsReady);
        Assert.Equal(2, grid.Cards.Count);
        Assert.Equal(FirstQuery, _harness.Queries(CatalogPath)[^1]);
    }

    [Fact]
    public async Task ChangeDuringALoad_ShowsOnlyTheNewestListing()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(CatalogPath, ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.OnlyPage), LibraryHarness.Held(release, LibraryPayloads.OnlyPage),
            ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.LastPage));
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;

        var stale = browse.SetSortCommand.ExecuteAsync(browse.SortOptions.Single(o => o.Label == "Year"));
        await browse.SetSortCommand.ExecuteAsync(browse.SortOptions.Single(o => o.Label == "Runtime"));
        release.SetResult();
        await stale;

        Assert.Equal("Runtime", browse.SortLabel);
        Assert.Equal(new[] { "Se7en" }, browse.Grid.Cards.Select(c => c.Title));
    }
}
