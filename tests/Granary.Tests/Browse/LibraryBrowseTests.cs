using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
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
        Assert.Equal("library_id=1&type=movie", Assert.Single(_harness.Queries("api/v2/catalog/filters")));
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
        var dateAdded = browse.SortOptions.Single(o => o.Label == "Date Added");
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
    [InlineData("Release Date", "-release_date")]
    [InlineData("Year", "-year")]
    [InlineData("Content Rating", "content_rating")]
    [InlineData("Duration", "-runtime")]
    [InlineData("IMDb Rating", "-rating_imdb")]
    [InlineData("TMDB Rating", "-rating_tmdb")]
    [InlineData("RT Critic Rating", "-rating_rt_critic")]
    [InlineData("RT Audience Rating", "-rating_rt_audience")]
    [InlineData("Resolution", "-resolution")]
    [InlineData("Bitrate", "-bitrate")]
    [InlineData("Progress", "-progress")]
    [InlineData("Date Viewed", "-date_viewed")]
    [InlineData("Plays", "-plays")]
    public async Task Sort_UsesTheServersFieldNames_InTheOrderEachStartsIn(string label, string sort)
    {
        var library = await _harness.OpenLibraryTabAsync();

        await library.Browse.SetSortCommand.ExecuteAsync(library.Browse.SortOptions.Single(o => o.Label == label));

        Assert.Contains($"&sort={sort}&", _harness.Queries(CatalogPath)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MovieLibrary_OffersTheWebsSorts_WithoutTheEpisodeOnes()
    {
        var library = await _harness.OpenLibraryTabAsync();

        Assert.Equal(
        [
            "Title", "Date Added", "Release Date", "Year", "Content Rating", "Duration", "IMDb Rating", "TMDB Rating", "RT Critic Rating",
            "RT Audience Rating", "Resolution", "Bitrate", "Progress", "Date Viewed", "Plays",
        ], library.Browse.SortOptions.Select(o => o.Label));
        Assert.Equal(1, _harness.Count(LibraryHarness.RatingsPath));
    }

    [Fact]
    public async Task SeriesLibrary_AlsoSortsByItsLatestEpisode()
    {
        _harness.Answer("api/v2/library/2/sections", LibraryPayloads.Sections);
        _harness.Answer("api/v2/library/2/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);
        var library = await _harness.OpenLibraryTabAsync("2");
        var browse = library.Browse;

        Assert.Equal(["Title", "Date Added", "Release Date", "Latest Episode Air Date", "Latest Episode Added", "Year"],
            browse.SortOptions.Take(6).Select(o => o.Label));

        await browse.SetSortCommand.ExecuteAsync(browse.SortOptions.Single(o => o.Label == "Latest Episode Added"));

        Assert.Contains("&sort=-latest_episode_added&", _harness.Queries(CatalogPath)[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "state": "available", "sources": [ { "source": "imdb", "name": "IMDb" }, { "source": "rt_critic", "name": "Tomatometer" } ] }""",
        "RT Critic Rating")]
    [InlineData("""{ "state": "available", "sources": [ { "source": "imdb", "name": "IMDb" } ] }""", "")]
    [InlineData("""{ "state": "disabled", "sources": [] }""", "")]
    public async Task RottenTomatoesSorts_OnlyForTheSourcesTheServerShows(string capability, string rtSorts)
    {
        _harness.Answer(LibraryHarness.RatingsPath, capability);

        var library = await _harness.OpenLibraryTabAsync();

        Assert.Equal(rtSorts, string.Join(",", library.Browse.SortOptions.Where(o => o.RatingSource is not null).Select(o => o.Label)));
        Assert.Contains(library.Browse.SortOptions, o => o.Label == "IMDb Rating");
    }

    [Fact]
    public async Task AServerWithoutRatings_OffersNoRottenTomatoesSorts()
    {
        _harness.Answer(LibraryHarness.RatingsPath, ScriptedSilo.Problem(404, "not_found"));

        var library = await _harness.OpenLibraryTabAsync();

        Assert.DoesNotContain(library.Browse.SortOptions, o => o.RatingSource is not null);
        Assert.Equal(2, library.Browse.Grid.Cards.Count);
    }

    [Fact]
    public async Task ARememberedSortTheLibraryDoesNotOffer_OpensByTitle()
    {
        var library = await _harness.OpenLibraryTabAsync();

        library.Browse.Restore(new LibraryBrowseState(CatalogSortFields.LastAirDate, true, LibraryFilterSet.None));

        Assert.True(library.Browse.Sort.IsTitle);
        Assert.False(library.Browse.IsDescending);
    }

    [Fact]
    public async Task ARememberedRatingSort_StaysWhileItsSourceIsHidden_UntilAnotherIsPicked()
    {
        _harness.Answer(LibraryHarness.RatingsPath, ScriptedSilo.Problem(404, "not_found"));
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;

        browse.Restore(new LibraryBrowseState(CatalogSortFields.RatingRtCritic, true, LibraryFilterSet.None));

        Assert.Equal("RT Critic Rating", browse.SortLabel);
        Assert.Contains(browse.SortOptions, o => o.Field == CatalogSortFields.RatingRtCritic);

        await browse.SetSortCommand.ExecuteAsync(browse.SortOptions.Single(o => o.Label == "Year"));

        Assert.DoesNotContain(browse.SortOptions, o => o.Field == CatalogSortFields.RatingRtCritic);
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
    public async Task Filters_YearsAndRatings_NarrowTheSimpleListing()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;
        Assert.Equal(new[] { "PG-13", "R" }, filters.RatingOptions.Select(o => o.Value));

        filters.BeginEdit();
        foreach (var rating in filters.RatingOptions)
        {
            rating.IsSelected = true;
        }
        filters.DraftYearFrom = 1999;
        filters.DraftYearTo = 1990.4;
        await filters.ApplyCommand.ExecuteAsync(null);

        Assert.Equal($"library_id=1&type=movie&year_min=1990&year_max=1999&content_rating=PG-13&content_rating=R&sort=title&limit=60&{Images}",
            _harness.Queries(CatalogPath)[^1]);
        Assert.Empty(_harness.QueryBodies());
        Assert.Equal(3, filters.ActiveCount);
        Assert.True(filters.HasActiveFilters);
        Assert.True(library.Browse.IsNarrowed);
    }

    [Fact]
    public async Task Filters_AreTheLetterIndexsRuleGroups_AsTheServerMakesThem()
    {
        // GET /catalog turns the years into one group that must all match, and several ratings into one of which any
        // may: the index counts the same titles the pages list.
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;
        filters.BeginEdit();
        foreach (var rating in filters.RatingOptions)
        {
            rating.IsSelected = true;
        }
        filters.DraftYearFrom = 1990;
        filters.DraftYearTo = 1999;

        await filters.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("""
            {"library_id":"1","type":"movie","groups":[{"match":"all","rules":[{"field":"year","op":"between","value":[1990,1999]}]},{"match":"any","rules":[{"field":"content_rating","op":"is","value":"PG-13"},{"field":"content_rating","op":"is","value":"R"}]}],"order":"asc"}
            """, _harness.LetterBodies()[^1]);
    }

    [Fact]
    public async Task Filters_Genres_TakeTheGridToTheRuleQuery_WithTheYearsAndRatingsAsGroupsBeforeThem()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;
        Assert.Equal(new[] { "Crime", "Drama" }, filters.GenreOptions.Select(o => o.Label));

        filters.BeginEdit();
        filters.GenreOptions[0].IsSelected = true;
        filters.GenreOptions[1].IsSelected = true;
        filters.RatingOptions[1].IsSelected = true;
        filters.DraftYearFrom = 1990;
        filters.DraftYearTo = 1999;
        await filters.ApplyCommand.ExecuteAsync(null);

        const string Groups = """
            "groups":[{"match":"all","rules":[{"field":"year","op":"between","value":[1990,1999]}]},{"match":"all","rules":[{"field":"content_rating","op":"is","value":"R"}]},{"match":"any","rules":[{"field":"genre","op":"contains","value":"Crime"},{"field":"genre","op":"contains","value":"Drama"}]}]
            """;
        var sent = _harness.Silo.SentTo(HttpMethod.Post, LibraryHarness.QueryPath);
        Assert.Equal("?" + Images, Assert.Single(sent).Uri.Query);
        Assert.Equal($$"""{"library_id":"1","type":"movie",{{Groups}},"sort":"title","order":"asc","limit":60}""", sent[0].Body);
        Assert.Equal($$"""{"library_id":"1","type":"movie",{{Groups}},"order":"asc"}""", _harness.LetterBodies()[^1]);
        Assert.Equal(1, _harness.Count(CatalogPath));
        Assert.Equal(4, filters.ActiveCount);
        Assert.Equal(new[] { "Alien", "Heat" }, library.Browse.Grid.Cards.Select(c => c.Title));
    }

    [Fact]
    public async Task Filters_DecadesQualityLanguagesAndWatchStatus_AreTheirRuleGroups_InTheFlyoutsOrder()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var browse = library.Browse;
        var filters = browse.Filters;
        await browse.SetSortCommand.ExecuteAsync(browse.SortOptions.Single(o => o.Label == "Year"));

        filters.BeginEdit();
        filters.DecadeOptions.Single(o => o.Label == "1990s").IsSelected = true;
        filters.VideoQualityOptions.Single(o => o.Label == "4K").IsSelected = true;
        filters.VideoQualityOptions.Single(o => o.Label == "DOVI").IsSelected = true;
        filters.OriginalLanguageOptions.Single(o => o.Label == "French").IsSelected = true;
        filters.AudioLanguageOptions.Single(o => o.Label == "German").IsSelected = true;
        filters.DraftWatchStatusIndex = LibraryFiltersViewModel.WatchStatusChoices.ToList().IndexOf("Unwatched");
        await filters.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("""
            {"library_id":"1","type":"movie","groups":[{"match":"any","rules":[{"field":"year","op":"between","value":[1990,1999]}]},{"match":"all","rules":[{"field":"resolution","op":"is","value":"2160p"},{"field":"dolby_vision","op":"is","value":true}]},{"match":"any","rules":[{"field":"original_language","op":"is","value":"fr"}]},{"match":"any","rules":[{"field":"audio_language","op":"is","value":"de"}]},{"match":"all","rules":[{"field":"watched","op":"is","value":false},{"field":"in_progress","op":"is","value":false}]}],"sort":"year","order":"desc","limit":60}
            """, Assert.Single(_harness.QueryBodies()));
        Assert.Equal(6, filters.ActiveCount);
        Assert.Equal(LibraryWatchStatus.Unwatched, filters.Applied.WatchStatus);
    }

    [Fact]
    public async Task Filters_OfferTheLanguagesByName_AndLeaveOutAFacetWithoutValues()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;

        Assert.Equal(new[] { "English", "French" }, filters.OriginalLanguageOptions.Select(o => o.Label));
        Assert.Equal(new[] { "en", "fr" }, filters.OriginalLanguageOptions.Select(o => o.Value));
        Assert.Equal(new[] { "English", "German" }, filters.AudioLanguageOptions.Select(o => o.Label));
        Assert.True(filters.HasAudioLanguages);
        Assert.False(filters.HasSubtitleLanguages);
        Assert.Equal(new[] { "4K", "HDR", "DOVI" }, filters.VideoQualityOptions.Select(o => o.Label));
        Assert.Equal("1900s", filters.DecadeOptions[^1].Label);
        Assert.Equal(new[] { "Any", "Unwatched", "In Progress", "Watched" }, LibraryFiltersViewModel.WatchStatusChoices);
    }

    [Fact]
    public async Task Filters_CountEachPickedValue_OnceTheYearRange_AndOnceAWatchStatus()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;

        filters.BeginEdit();
        foreach (var option in filters.GenreOptions.Concat(filters.RatingOptions).Concat(filters.VideoQualityOptions)
            .Concat(filters.OriginalLanguageOptions).Concat(filters.AudioLanguageOptions).Concat(filters.DecadeOptions.Take(2)))
        {
            option.IsSelected = true;
        }
        filters.DraftYearFrom = 1980;
        filters.DraftYearTo = 2000;
        filters.DraftWatchStatusIndex = 3;
        await filters.ApplyCommand.ExecuteAsync(null);

        // 2 genres, 2 ratings, 3 qualities, 2 + 2 languages, 2 decades, the years, the watch status.
        Assert.Equal(15, filters.ActiveCount);
    }

    [Fact]
    public async Task Filters_ARememberedValueTheLibraryNoLongerHas_StillShows_SoItCanBeSwitchedOff()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;

        filters.Restore(new LibraryFilterSet { Genres = ["Western"], AudioLanguages = ["ja"] });

        Assert.Equal(new[] { "Crime", "Drama", "Western" }, filters.GenreOptions.Select(o => o.Value));
        Assert.True(filters.GenreOptions[^1].IsSelected);
        Assert.Equal("Japanese", filters.AudioLanguageOptions[^1].Label);
        filters.GenreOptions[^1].IsSelected = false;
        await filters.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "ja" }, filters.Applied.AudioLanguages);
        Assert.Empty(filters.Applied.Genres);
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
        filters.GenreOptions[0].IsSelected = true;
        filters.VideoQualityOptions[1].IsSelected = true;
        await filters.ApplyCommand.ExecuteAsync(null);

        await filters.ClearCommand.ExecuteAsync(null);

        Assert.Equal(0, filters.ActiveCount);
        Assert.DoesNotContain(filters.GenreOptions.Concat(filters.VideoQualityOptions), o => o.IsSelected);
        Assert.Equal(FirstQuery, _harness.Queries(CatalogPath)[^1]);
        Assert.Equal(AllTitlesLetters, _harness.LetterBodies()[^1]);
    }

    [Fact]
    public async Task Filters_ReopenedFlyout_ShowsTheAppliedOnesNotTheAbandonedDraft()
    {
        var library = await _harness.OpenLibraryTabAsync();
        var filters = library.Browse.Filters;
        filters.BeginEdit();
        filters.GenreOptions[0].IsSelected = true;
        filters.RatingOptions[1].IsSelected = true;
        await filters.ApplyCommand.ExecuteAsync(null);
        filters.BeginEdit();
        filters.GenreOptions[0].IsSelected = false;
        filters.GenreOptions[1].IsSelected = true;
        filters.DecadeOptions[0].IsSelected = true;
        filters.DraftWatchStatusIndex = 2;
        filters.DraftYearFrom = 1980;

        filters.BeginEdit();

        Assert.Equal(new[] { true, false }, filters.GenreOptions.Select(o => o.IsSelected));
        Assert.False(filters.DecadeOptions[0].IsSelected);
        Assert.Equal(0, filters.DraftWatchStatusIndex);
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
        Assert.False(library.Browse.Filters.HasGenres);
        Assert.False(library.Browse.Filters.HasOriginalLanguages);
        Assert.Equal(2, library.Browse.Grid.Cards.Count);
    }

    [Fact]
    public async Task FailedLanguagesOfTheFiles_LeaveTheOtherOptions()
    {
        // The file-derived facets are the heavier read: without them the genres, ratings and original languages still show.
        _harness.Silo.On(HttpMethod.Get, "api/v2/catalog/filters", ScriptedSilo.Problem(400, "invalid_request"),
            ScriptedSilo.Json(HttpStatusCode.OK, """{ "genres": ["Crime"], "content_ratings": ["R"], "original_languages": ["en"] }"""));

        var library = await _harness.OpenLibraryTabAsync();

        var filters = library.Browse.Filters;
        Assert.Equal(["library_id=1&type=movie", "library_id=1&type=movie&skip_technical=true"], _harness.Queries("api/v2/catalog/filters"));
        Assert.False(filters.HasOptionsError);
        Assert.True(filters.HasGenres);
        Assert.True(filters.HasOriginalLanguages);
        Assert.False(filters.HasAudioLanguages);
    }

    [Fact]
    public async Task OptionsArrivingWhileTheFlyoutIsOpen_KeepTheDraft()
    {
        var release = new TaskCompletionSource();
        _harness.Answer("api/v2/catalog/filters", LibraryHarness.Held(release, LibraryPayloads.Filters));
        var library = _harness.Create();
        await library.ActivateAsync("1");
        var tab = library.SelectTabCommand.ExecuteAsync(LibraryTab.Library);
        var filters = library.Browse.Filters;
        filters.Restore(new LibraryFilterSet { Genres = ["Drama"] });
        filters.BeginEdit();
        filters.GenreOptions.Single(o => o.Value == "Drama").IsSelected = false;

        release.SetResult();
        await tab;

        Assert.Equal(new[] { "Crime", "Drama" }, filters.GenreOptions.Select(o => o.Value));
        Assert.DoesNotContain(filters.GenreOptions, o => o.IsSelected);
        Assert.Equal(["Drama"], filters.Applied.Genres);
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
        browse.Filters.GenreOptions[0].IsSelected = true;
        _harness.Silo.On(HttpMethod.Post, LibraryHarness.QueryPath, ScriptedSilo.Json(HttpStatusCode.OK, LibraryPayloads.EmptyPage));
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
        await browse.SetSortCommand.ExecuteAsync(browse.SortOptions.Single(o => o.Label == "Duration"));
        release.SetResult();
        await stale;

        Assert.Equal("Duration", browse.SortLabel);
        Assert.Equal(new[] { "Se7en" }, browse.Grid.Cards.Select(c => c.Title));
    }
}
