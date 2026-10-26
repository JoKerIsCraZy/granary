using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The sidebar's sections that fold away, and how they are kept across starts.</summary>
public sealed class SidebarSectionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LogSink _logs = new();

    private string FilePath => Path.Combine(_directory, "sidebar.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SidebarSections Create() => new(FilePath, _logs.For<SidebarSections>());

    [Fact]
    public void AtFirst_EverySectionIsOpen()
    {
        var sections = Create();

        Assert.True(sections.IsLibrariesExpanded);
        Assert.True(sections.IsDiscoverExpanded);
        Assert.True(sections.IsYourStuffExpanded);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task AFoldedSection_StaysFoldedAtTheNextStart()
    {
        var sections = Create();

        await sections.ToggleCommand.ExecuteAsync(SidebarSection.Discover);
        await sections.ToggleCommand.ExecuteAsync(SidebarSection.YourStuff);
        await sections.ToggleCommand.ExecuteAsync(SidebarSection.YourStuff);

        Assert.False(sections.IsDiscoverExpanded);
        Assert.True(sections.IsYourStuffExpanded);
        var next = Create();
        Assert.False(next.IsDiscoverExpanded);
        Assert.True(next.IsLibrariesExpanded);
        Assert.True(next.IsYourStuffExpanded);
        Assert.Contains("\"collapsed\"", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllThreeFolded_StayFolded_AndSayWhenTheyChange()
    {
        var sections = Create();
        var changed = new List<string?>();
        sections.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        foreach (var section in Enum.GetValues<SidebarSection>())
        {
            await sections.ToggleCommand.ExecuteAsync(section);
        }

        Assert.Equal([nameof(SidebarSections.IsLibrariesExpanded), nameof(SidebarSections.IsDiscoverExpanded), nameof(SidebarSections.IsYourStuffExpanded)],
            changed);
        var next = Create();
        Assert.All(Enum.GetValues<SidebarSection>(), s => Assert.False(next.IsExpanded(s)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{ "collapsed": null }""")]
    [InlineData("""{ "collapsed": [1] }""")]
    [InlineData("""{ "collapsed": "discover" }""")]
    public void AFileWithoutAList_LeavesEverySectionOpen(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json);

        var sections = Create();

        Assert.All(Enum.GetValues<SidebarSection>(), s => Assert.True(sections.IsExpanded(s)));
    }

    [Fact]
    public async Task TogglesInARow_SaveTheLastState()
    {
        var sections = Create();

        await Task.WhenAll(
            sections.ToggleAsync(SidebarSection.Libraries),
            sections.ToggleAsync(SidebarSection.Discover),
            sections.ToggleAsync(SidebarSection.Libraries));

        var next = Create();
        Assert.True(next.IsLibrariesExpanded);
        Assert.False(next.IsDiscoverExpanded);
    }

    [Fact]
    public void AFileThatIsNotOne_LeavesEverySectionOpen()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        var sections = Create();

        Assert.True(sections.IsLibrariesExpanded && sections.IsDiscoverExpanded && sections.IsYourStuffExpanded);
        Assert.Contains("could not be read", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public void ASectionThisAppDoesNotKnow_IsLeftOut()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{ "collapsed": ["apps", "libraries", null] }""");

        var sections = Create();

        Assert.False(sections.IsLibrariesExpanded);
        Assert.True(sections.IsDiscoverExpanded);
    }

    [Fact]
    public async Task AFileThatCannotBeWritten_StillFoldsTheSection()
    {
        // A directory where the file should be: the save fails.
        Directory.CreateDirectory(FilePath);
        var sections = Create();

        await sections.ToggleCommand.ExecuteAsync(SidebarSection.Libraries);

        Assert.False(sections.IsLibrariesExpanded);
        Assert.Contains("could not be saved", _logs.All, StringComparison.Ordinal);
        Assert.False(File.Exists(FilePath + ".tmp"));

        // A failed save does not hold up the next one.
        await sections.ToggleCommand.ExecuteAsync(SidebarSection.Discover);
        Assert.False(sections.IsDiscoverExpanded);
    }
}
