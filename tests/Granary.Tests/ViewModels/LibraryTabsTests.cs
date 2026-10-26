using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The tab each library was last left on, and how it is kept across starts.</summary>
public sealed class LibraryTabsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LogSink _logs = new();

    private string FilePath => Path.Combine(_directory, "library-tabs.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private LibraryTabs Create() => new(FilePath, _logs.For<LibraryTabs>());

    [Fact]
    public void AtFirst_NoLibraryHasATab()
    {
        var tabs = Create();

        Assert.Null(tabs.Recall("server-1", "7", "1"));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task ATab_IsRecalledAtTheNextStart_ForItsServerProfileAndLibraryOnly()
    {
        var tabs = Create();

        await tabs.RememberAsync("server-1", "7", "1", LibraryTab.Library);
        await tabs.RememberAsync("server-1", "7", "2", LibraryTab.Collections);
        await tabs.RememberAsync("server-1", null, "1", LibraryTab.Collections);
        await tabs.RememberAsync("server-1", "7", "2", LibraryTab.Recommended);

        var next = Create();
        Assert.Equal(LibraryTab.Library, next.Recall("server-1", "7", "1"));
        Assert.Equal(LibraryTab.Recommended, next.Recall("server-1", "7", "2"));
        Assert.Equal(LibraryTab.Collections, next.Recall("server-1", null, "1"));
        Assert.Null(next.Recall("server-1", "8", "1"));
        Assert.Null(next.Recall("server-2", "7", "1"));
        Assert.Contains("\"library\"", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{ "tabs": null }""")]
    [InlineData("""{ "tabs": "library" }""")]
    [InlineData("""{ "tabs": [null, { "server": "server-1", "library": "1", "tab": "grid" }, { "library": "1", "tab": "library" }] }""")]
    public void AFileWithoutUsableTabs_RemembersNothing(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json);

        var tabs = Create();

        Assert.Null(tabs.Recall("server-1", null, "1"));
    }

    [Fact]
    public async Task AFileThatCannotBeWritten_IsLogged_AndTheTabIsKeptForThisRun()
    {
        // A directory where the file should be: the save fails.
        Directory.CreateDirectory(FilePath);
        var tabs = Create();

        await tabs.RememberAsync("server-1", "7", "1", LibraryTab.Collections);

        Assert.Equal(LibraryTab.Collections, tabs.Recall("server-1", "7", "1"));
        Assert.Contains("could not be saved", _logs.All, StringComparison.Ordinal);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}
