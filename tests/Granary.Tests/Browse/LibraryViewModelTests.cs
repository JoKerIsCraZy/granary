using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

public sealed class LibraryViewModelTests
{
    private const string LibrariesPath = "api/v2/user/libraries";

    private readonly LibraryHarness _harness = new();

    [Fact]
    public async Task Activate_ResolvesTheLibraryFromItsId()
    {
        var library = _harness.Create();

        await library.ActivateAsync("1");

        Assert.Equal(LibraryLoadState.Ready, library.State);
        Assert.Equal("Filme", library.LibraryName);
        Assert.Equal("LIBRARY / FILME", library.Breadcrumb);
        Assert.Equal(LibraryTab.Recommended, library.SelectedTab);
        Assert.True(library.IsRecommendedTab);
        Assert.Equal(LibraryHarness.ImageQuery, Assert.Single(_harness.Queries("api/v2/library/1/sections")));
    }

    [Fact]
    public async Task Activate_UnknownId_SaysTheLibraryIsNotAvailable()
    {
        var library = _harness.Create();

        await library.ActivateAsync("99");

        Assert.Equal(LibraryLoadState.Unavailable, library.State);
        Assert.True(library.IsUnavailable);
        Assert.Equal("", library.LibraryName);
        Assert.Equal(0, _harness.Count("api/v2/library/99/sections"));
    }

    [Fact]
    public async Task Activate_AHiddenLibrary_SaysItIsNotAvailable()
    {
        _harness.Answer(LibraryHarness.SettingsPath,
            """{ "items": [{ "key": "ui.disabled_library_ids", "value": [1] }, { "key": "ui.library_order", "value": null }], "revision": 2 }""");
        var library = _harness.Create();

        await library.ActivateAsync("1");

        Assert.Equal(LibraryLoadState.Unavailable, library.State);
        Assert.Equal(0, _harness.Count("api/v2/library/1/sections"));
    }

    [Fact]
    public async Task Activate_WithoutAnId_SaysTheLibraryIsNotAvailable()
    {
        var library = _harness.Create();

        await library.ActivateAsync(null);

        Assert.Equal(LibraryLoadState.Unavailable, library.State);
        Assert.Equal(0, _harness.Count(LibrariesPath));
    }

    [Fact]
    public async Task FailedLibraryList_ShowsAFriendlyTextAndRetryRecovers()
    {
        _harness.Answer(LibrariesPath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable), ScriptedSilo.Json(System.Net.HttpStatusCode.OK, LibraryPayloads.Libraries));
        var library = _harness.Create();

        await library.ActivateAsync("1");

        Assert.Equal(LibraryLoadState.Error, library.State);
        Assert.Equal(LibraryErrors.Busy, library.ErrorMessage);
        Assert.DoesNotContain("Silo API", library.ErrorMessage, StringComparison.Ordinal);

        await library.RetryCommand.ExecuteAsync(null);

        Assert.Equal(LibraryLoadState.Ready, library.State);
        Assert.Equal("", library.ErrorMessage);
    }

    [Fact]
    public async Task NetworkFailure_IsWordedWithoutTheExceptionText()
    {
        _harness.Answer(LibrariesPath, ScriptedSilo.NetworkError());
        var library = _harness.Create();

        await library.ActivateAsync("1");

        Assert.Equal(LibraryErrors.Unreachable, library.ErrorMessage);
        Assert.DoesNotContain("refused", library.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameLibraryAgain_KeepsTheTabAndAsksNothing()
    {
        var library = await _harness.OpenLibraryTabAsync();
        library.Deactivate();
        int requests = _harness.Silo.Sent.Count;

        await library.ActivateAsync("1");

        Assert.Equal(LibraryTab.Library, library.SelectedTab);
        Assert.Equal(2, library.Browse.Grid.Cards.Count);
        Assert.Equal(requests, _harness.Silo.Sent.Count);
    }

    [Fact]
    public async Task AnotherLibrary_StartsOverOnRecommended()
    {
        _harness.Answer("api/v2/library/2/sections", LibraryPayloads.Sections);
        _harness.Answer("api/v2/library/2/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);
        var library = await _harness.OpenLibraryTabAsync();

        await library.ActivateAsync("2");

        Assert.Equal("Serien", library.LibraryName);
        Assert.Equal(LibraryTab.Recommended, library.SelectedTab);
        Assert.Empty(library.Browse.Grid.Cards);
        Assert.Equal(LibraryLoadState.Idle, library.Browse.Grid.State);
        Assert.Equal(1, _harness.Count("api/v2/library/2/sections"));
    }

    [Fact]
    public async Task ALibrary_OpensOnTheTabItWasLastLeftOn_AnotherOneOnRecommended()
    {
        string directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
        try
        {
            _harness.Tabs = new LibraryTabs(Path.Combine(directory, "library-tabs.json"), _harness.Logs.For<LibraryTabs>());
            _harness.Answer("api/v2/library/2/sections", LibraryPayloads.Sections);
            _harness.Answer("api/v2/library/2/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);
            var first = _harness.Create();
            await first.ActivateAsync("1");
            await first.SelectTabCommand.ExecuteAsync(LibraryTab.Collections);
            first.Deactivate();

            // The next start: a new store reads the file, a new page opens the library.
            _harness.Tabs = new LibraryTabs(Path.Combine(directory, "library-tabs.json"), _harness.Logs.For<LibraryTabs>());
            var library = _harness.Create();
            await library.ActivateAsync("1");

            Assert.Equal(LibraryTab.Collections, library.SelectedTab);
            Assert.NotEqual(LibraryLoadState.Idle, library.Collections.State);
            Assert.Equal(2, _harness.Count("api/v2/library/1/collections"));
            // Only the first page loaded the rows: this one opened straight on Collections.
            Assert.Equal(1, _harness.Count("api/v2/library/1/sections"));

            await library.ActivateAsync("2");
            Assert.Equal(LibraryTab.Recommended, library.SelectedTab);

            await library.ActivateAsync("1");
            Assert.Equal(LibraryTab.Collections, library.SelectedTab);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ALibrary_OpensSortedAndFilteredAsItWasLastLeft()
    {
        string directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(directory, "library-tabs.json");
            _harness.Tabs = new LibraryTabs(file, _harness.Logs.For<LibraryTabs>());
            var first = await _harness.OpenLibraryTabAsync();
            await first.Browse.SetSortCommand.ExecuteAsync(LibrarySortOption.All.Single(o => o.Field == CatalogSortFields.Year));
            var picked = first.Browse.Filters;
            picked.BeginEdit();
            picked.GenreOptions.Single(o => o.Value == "Drama").IsSelected = true;
            picked.RatingOptions.Single(o => o.Value == "R").IsSelected = true;
            picked.DraftYearFrom = 1990;
            picked.DecadeOptions.Single(o => o.Label == "1980s").IsSelected = true;
            picked.VideoQualityOptions.Single(o => o.Label == "HDR").IsSelected = true;
            picked.OriginalLanguageOptions.Single(o => o.Value == "fr").IsSelected = true;
            picked.AudioLanguageOptions.Single(o => o.Value == "de").IsSelected = true;
            picked.DraftWatchStatusIndex = (int)LibraryWatchStatus.InProgress;
            await picked.ApplyCommand.ExecuteAsync(null);
            first.Deactivate();

            // The next start: a new store reads the file, a new page opens the library on the grid, as it was.
            _harness.Tabs = new LibraryTabs(file, _harness.Logs.For<LibraryTabs>());
            var library = _harness.Create();
            await library.ActivateAsync("1");

            Assert.Equal(LibraryTab.Library, library.SelectedTab);
            Assert.Equal(CatalogSortFields.Year, library.Browse.Sort.Field);
            Assert.True(library.Browse.IsDescending);
            var filters = library.Browse.Filters.Applied;
            Assert.Equal(["Drama"], filters.Genres);
            Assert.Equal(["R"], filters.ContentRatings);
            Assert.Equal(1990, filters.YearFrom);
            Assert.Equal([1980], filters.Decades);
            Assert.True(filters.Hdr);
            Assert.Equal(["fr"], filters.OriginalLanguages);
            Assert.Equal(["de"], filters.AudioLanguages);
            Assert.Equal(LibraryWatchStatus.InProgress, filters.WatchStatus);
            Assert.Equal(8, library.Browse.Filters.ActiveCount);
            Assert.True(library.Browse.Filters.GenreOptions.Single(o => o.Value == "Drama").IsSelected);
            string body = _harness.QueryBodies()[^1];
            Assert.Contains("\"sort\":\"year\",\"order\":\"desc\"", body, StringComparison.Ordinal);
            Assert.Contains("{\"field\":\"genre\",\"op\":\"contains\",\"value\":\"Drama\"}", body, StringComparison.Ordinal);
            Assert.Contains("{\"field\":\"year\",\"op\":\"gte\",\"value\":1990}", body, StringComparison.Ordinal);
            Assert.Contains("{\"field\":\"in_progress\",\"op\":\"is\",\"value\":true}", body, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AnOlderFile_KeepsItsTabs_AndTheLibraryOpensByTitle()
    {
        string directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(directory, "library-tabs.json");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(file, """{ "tabs": [ { "server": "srv1", "profile": "7", "library": "1", "tab": "library" } ] }""",
                TestContext.Current.CancellationToken);
            _harness.Tabs = new LibraryTabs(file, _harness.Logs.For<LibraryTabs>());

            var library = _harness.Create();
            await library.ActivateAsync("1");

            Assert.Equal(LibraryTab.Library, library.SelectedTab);
            Assert.True(library.Browse.Sort.IsTitle);
            Assert.False(library.Browse.IsNarrowed);
            Assert.Null(_harness.Tabs.RecallBrowse("srv1", "7", "1"));

            await library.Browse.SortDescendingCommand.ExecuteAsync(null);
            var reread = new LibraryTabs(file, _harness.Logs.For<LibraryTabs>());
            Assert.Equal(LibraryTab.Library, reread.Recall("srv1", "7", "1"));
            Assert.Equal(new LibraryBrowseState(CatalogSortFields.Title, true, LibraryFilterSet.None), reread.RecallBrowse("srv1", "7", "1"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AnOlderFilesOneGenre_IsTheOneGenrePicked_AndTheOtherFiltersAreOff()
    {
        string directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(directory, "library-tabs.json");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(file, """
                { "tabs": [ { "server": "srv1", "profile": "7", "library": "1", "tab": "library", "sort": "year", "descending": true,
                  "genre": "Drama", "content_ratings": ["R"], "year_from": 1990 } ] }
                """, TestContext.Current.CancellationToken);
            _harness.Tabs = new LibraryTabs(file, _harness.Logs.For<LibraryTabs>());

            var library = _harness.Create();
            await library.ActivateAsync("1");

            var filters = library.Browse.Filters.Applied;
            Assert.Equal(new LibraryFilterSet { Genres = ["Drama"], ContentRatings = ["R"], YearFrom = 1990 }, filters);
            Assert.Equal(3, library.Browse.Filters.ActiveCount);
            Assert.Contains("{\"field\":\"genre\",\"op\":\"contains\",\"value\":\"Drama\"}", _harness.QueryBodies()[^1], StringComparison.Ordinal);

            // Kept again, it is the list of genres.
            await library.Browse.SortAscendingCommand.ExecuteAsync(null);
            string saved = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.Contains("\"genres\": [", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("\"genre\":", saved, StringComparison.Ordinal);
            Assert.Equal(filters, new LibraryTabs(file, _harness.Logs.For<LibraryTabs>()).RecallBrowse("srv1", "7", "1")!.Filters);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ADamagedFile_LosesOnlyItsBadEntry_AndBadYearsAreTakenAsYears()
    {
        string directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(directory, "library-tabs.json");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(file, """
                { "tabs": [
                  { "server": "srv1", "profile": "7", "library": "2", "tab": "library", "descending": "yes" },
                  { "server": "srv1", "profile": "7", "library": "1", "tab": "library", "sort": "year", "year_from": -5, "year_to": 20000 }
                ] }
                """, TestContext.Current.CancellationToken);
            _harness.Tabs = new LibraryTabs(file, _harness.Logs.For<LibraryTabs>());

            var library = _harness.Create();
            await library.ActivateAsync("1");

            Assert.Equal(LibraryTab.Library, library.SelectedTab);
            Assert.Null(library.Browse.Filters.Applied.YearFrom);
            Assert.Equal(LibraryFiltersViewModel.MaximumYear, library.Browse.Filters.Applied.YearTo);
            Assert.Contains("year_max=9999", _harness.Queries(LibraryHarness.CatalogPath)[^1], StringComparison.Ordinal);
            Assert.Null(_harness.Tabs.Recall("srv1", "7", "2"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RememberedYears_AreTakenAsApplyTakesThem()
    {
        var filters = new LibraryFiltersViewModel(() => Task.CompletedTask, _harness.Logs.For<LibraryViewModel>());

        filters.Restore(new LibraryFilterSet { YearFrom = 2010, YearTo = 1990 });
        Assert.Equal((1990, 2010), (filters.Applied.YearFrom!.Value, filters.Applied.YearTo!.Value));

        filters.Restore(new LibraryFilterSet { YearFrom = 0, YearTo = -3 });
        Assert.Null(filters.Applied.YearFrom);
        Assert.Null(filters.Applied.YearTo);
        Assert.Equal(0, filters.ActiveCount);
    }

    [Fact]
    public async Task ASortOnlyKeptForAnotherProfile_LeavesThisOneByTitle()
    {
        string directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(directory, "library-tabs.json");
            _harness.Tabs = new LibraryTabs(file, _harness.Logs.For<LibraryTabs>());
            await _harness.Tabs.RememberBrowseAsync("srv1", "8", "1",
                new LibraryBrowseState(CatalogSortFields.Year, true, new LibraryFilterSet { Genres = ["Crime"] }));

            var library = await _harness.OpenLibraryTabAsync();

            Assert.True(library.Browse.Sort.IsTitle);
            Assert.Empty(library.Browse.Filters.Applied.Genres);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProfileChange_ReloadsTheShownLibraryOnItsTab()
    {
        var library = await _harness.OpenLibraryTabAsync();
        bool changed = false;
        library.LibraryChanged += (_, _) => changed = true;

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();
        await Auth.AuthPipeline.Eventually(() => _harness.Count(LibrariesPath) == 2 && library.Browse.Grid.IsReady);

        Assert.True(changed);
        Assert.Equal(LibraryTab.Library, library.SelectedTab);
        Assert.Equal(2, _harness.Count(LibraryHarness.CatalogPath));
    }

    [Fact]
    public async Task VisitAfterAnHour_ReloadsTheSameTabForFreshArtwork()
    {
        var library = await _harness.OpenLibraryTabAsync();
        library.Deactivate();
        _harness.Clock.Advance(TimeSpan.FromMinutes(59));
        await library.ActivateAsync("1");
        Assert.Equal(1, _harness.Count(LibrariesPath));
        library.Deactivate();

        _harness.Clock.Advance(LibraryViewModel.RefreshAfter);
        await library.ActivateAsync("1");

        Assert.Equal(2, _harness.Count(LibrariesPath));
        Assert.Equal(LibraryTab.Library, library.SelectedTab);
        Assert.Equal(2, _harness.Count(LibraryHarness.CatalogPath));
        Assert.True(library.Browse.Grid.IsReady);
    }

    [Fact]
    public async Task NonSiloAnswer_IsWordedAsAnUnreachableServer()
    {
        // A captive portal or proxy page: SiloHttp reports it as an invalid response.
        _harness.Answer(LibrariesPath, "{ not json");
        var library = _harness.Create();

        await library.ActivateAsync("1");

        Assert.Equal(LibraryLoadState.Error, library.State);
        Assert.Equal(LibraryErrors.Unreachable, library.ErrorMessage);
    }

    [Fact]
    public async Task FailureOfAReplacedLoad_LeavesTheNewLibraryAlone()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(LibrariesPath, async () =>
        {
            await release.Task;
            throw new HttpRequestException("connection reset");
        }, ScriptedSilo.Json(System.Net.HttpStatusCode.OK, LibraryPayloads.Libraries));
        _harness.Answer("api/v2/library/2/sections", LibraryPayloads.Sections);
        _harness.Answer("api/v2/library/2/sections/recently_added/items", LibraryPayloads.RecentlyAddedItems);
        var library = _harness.Create();

        var replaced = library.ActivateAsync("1");
        await library.ActivateAsync("2");
        release.SetResult();
        await replaced;

        Assert.Equal(LibraryLoadState.Ready, library.State);
        Assert.Equal("Serien", library.LibraryName);
        Assert.Equal("", library.ErrorMessage);
    }

    [Fact]
    public async Task AccountUpdate_DoesNotReload()
    {
        var library = _harness.Create();
        await library.ActivateAsync("1");

        _harness.Session.RaiseChanged();

        Assert.Equal(1, _harness.Count(LibrariesPath));
    }

    [Fact]
    public async Task ServerChange_WhileAway_ReloadsOnTheNextVisit()
    {
        var library = _harness.Create();
        await library.ActivateAsync("1");
        library.Deactivate();

        _harness.Session.ActiveServer = ScriptedSilo.Entry("srv2", ScriptedSilo.Base);
        _harness.Session.RaiseChanged();
        Assert.Equal(1, _harness.Count(LibrariesPath));

        await library.ActivateAsync("1");

        Assert.Equal(2, _harness.Count(LibrariesPath));
        Assert.Equal(LibraryLoadState.Ready, library.State);
    }

    [Fact]
    public async Task SignedOut_ShowsNothingAndAsksNothing()
    {
        _harness.Session.State = SessionState.SignedOut;
        var library = _harness.Create();

        await library.ActivateAsync("1");

        Assert.Equal(LibraryLoadState.Idle, library.State);
        Assert.Empty(_harness.Silo.Sent);
    }

    [Fact]
    public async Task Tabs_LoadTheFirstTimeTheyShow()
    {
        var library = _harness.Create();
        await library.ActivateAsync("1");
        Assert.Equal(0, _harness.Count(LibraryHarness.CatalogPath));
        Assert.Equal(0, _harness.Count("api/v2/library/1/collections"));

        await library.SelectTabCommand.ExecuteAsync(LibraryTab.Collections);
        await library.SelectTabCommand.ExecuteAsync(LibraryTab.Recommended);
        await library.SelectTabCommand.ExecuteAsync(LibraryTab.Collections);

        Assert.True(library.IsCollectionsTab);
        Assert.Equal(1, _harness.Count("api/v2/library/1/collections"));
        Assert.Equal(1, _harness.Count("api/v2/library/1/sections"));
    }

    [Fact]
    public async Task OpenItem_NavigatesToTheTitle()
    {
        var library = await _harness.OpenLibraryTabAsync();

        library.OpenItemCommand.Execute(library.Browse.Grid.Cards[1]);

        Assert.Equal((AppRoute.Item, (object?)"movie:heat-1995"), _harness.Navigation.Navigations[^1]);
    }

    [Fact]
    public async Task DisplayScale_ScalesTheRequestedArtwork()
    {
        var library = _harness.Create();
        library.DisplayScale = 1.5;

        await library.ActivateAsync("1");

        Assert.Equal("poster_width=513&backdrop_width=1920&logo_width=750&still_width=600&photo_width=278",
            Assert.Single(_harness.Queries("api/v2/library/1/sections")));
    }
}
