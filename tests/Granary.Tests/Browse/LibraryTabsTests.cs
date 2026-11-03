using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.Browse;

/// <summary><c>library-tabs.json</c>: a library's sort and filters, written and read again.</summary>
public sealed class LibraryTabsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LogSink _logs = new();

    private string File => Path.Combine(_directory, "library-tabs.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task EveryFilter_SurvivesWritingAndReadingAgain()
    {
        var state = new LibraryBrowseState(CatalogSortFields.RatingRtAudience, true, new LibraryFilterSet
        {
            Genres = ["Crime", "Drama"], Decades = [1980, 1990], YearFrom = 1975, YearTo = 2005, ContentRatings = ["PG-13", "R"],
            FourK = true, Hdr = true, DolbyVision = true, OriginalLanguages = ["fr"], AudioLanguages = ["de", "en"], SubtitleLanguages = ["pt-br"],
            WatchStatus = LibraryWatchStatus.InProgress,
        });
        await new LibraryTabs(File, _logs.For<LibraryTabs>()).RememberBrowseAsync("srv1", "7", "1", state);

        var reread = new LibraryTabs(File, _logs.For<LibraryTabs>()).RecallBrowse("srv1", "7", "1");

        Assert.Equal(state, reread);
        string saved = await System.IO.File.ReadAllTextAsync(File, TestContext.Current.CancellationToken);
        Assert.Contains("\"four_k\": true", saved, StringComparison.Ordinal);
        Assert.Contains("\"watch_status\": \"in_progress\"", saved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LibraryWatchStatus.Unwatched)]
    [InlineData(LibraryWatchStatus.Watched)]
    public async Task EachWatchStatus_SurvivesWritingAndReadingAgain(LibraryWatchStatus status)
    {
        var state = new LibraryBrowseState(CatalogSortFields.Title, false, new LibraryFilterSet { WatchStatus = status });
        await new LibraryTabs(File, _logs.For<LibraryTabs>()).RememberBrowseAsync("srv1", "7", "1", state);

        Assert.Equal(state, new LibraryTabs(File, _logs.For<LibraryTabs>()).RecallBrowse("srv1", "7", "1"));
    }

    [Fact]
    public async Task UnfilteredLibraries_WriteNoFilterMembers()
    {
        await new LibraryTabs(File, _logs.For<LibraryTabs>()).RememberBrowseAsync("srv1", "7", "1", LibraryBrowseState.Default);

        string saved = await System.IO.File.ReadAllTextAsync(File, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("genres", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("four_k", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("watch_status", saved, StringComparison.Ordinal);
        Assert.Equal(LibraryBrowseState.Default, new LibraryTabs(File, _logs.For<LibraryTabs>()).RecallBrowse("srv1", "7", "1"));
    }

    [Theory]
    [InlineData("\"year_from\": \"1990\"")]
    [InlineData("\"decades\": [\"1990s\"]")]
    [InlineData("\"four_k\": \"yes\"")]
    [InlineData("\"audio_languages\": \"de\"")]
    public async Task ADamagedEntry_IsTheOnlyOneLost(string damage)
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, $$"""
            { "tabs": [
              { "server": "srv1", "profile": "7", "library": "1", "tab": "library", "sort": "year", {{damage}} },
              { "server": "srv1", "profile": "7", "library": "2", "tab": "library", "sort": "year", "hdr": true, "watch_status": "watched" }
            ] }
            """, TestContext.Current.CancellationToken);

        var tabs = new LibraryTabs(File, _logs.For<LibraryTabs>());

        Assert.Null(tabs.Recall("srv1", "7", "1"));
        Assert.Null(tabs.RecallBrowse("srv1", "7", "1"));
        Assert.Equal(new LibraryFilterSet { Hdr = true, WatchStatus = LibraryWatchStatus.Watched }, tabs.RecallBrowse("srv1", "7", "2")!.Filters);
        Assert.Contains(_logs.Lines, l => l.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownOrMissingValues_ReadAsNoFilter()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, """
            { "tabs": [ { "server": "srv1", "profile": "7", "library": "1", "sort": "title", "watch_status": "binged", "decades": [null, 1995, 2000],
              "genres": ["Crime", null, ""], "four_k": null } ] }
            """, TestContext.Current.CancellationToken);

        var filters = new LibraryTabs(File, _logs.For<LibraryTabs>()).RecallBrowse("srv1", "7", "1")!.Filters;

        Assert.Equal(new LibraryFilterSet { Genres = ["Crime"], Decades = [2000] }, filters);
    }
}
