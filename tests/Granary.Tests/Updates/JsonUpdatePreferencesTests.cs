using Granary.Core.Updates;
using Granary.Tests.Fakes;

namespace Granary.Tests.Updates;

/// <summary>Whether Granary updates itself on its own, as kept in the data directory across starts.</summary>
public sealed class JsonUpdatePreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LogSink _logs = new();

    private string FilePath => Path.Combine(_directory, "updates.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private JsonUpdatePreferences Create() => new(FilePath, _logs.For<JsonUpdatePreferences>());

    [Fact]
    public void AtFirst_UpdatesAreAutomatic()
    {
        var preferences = Create();

        Assert.True(preferences.IsAutomatic);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void TheFile_IsInTheDataDirectory()
    {
        Assert.Equal(Path.Combine(Granary.Core.AppPaths.DataDirectory, "updates.json"), JsonUpdatePreferences.DefaultPath);
    }

    [Fact]
    public async Task TurnedOff_StaysOffAtTheNextStart()
    {
        var preferences = Create();

        await preferences.SetAutomaticAsync(false);

        Assert.False(preferences.IsAutomatic);
        Assert.False(Create().IsAutomatic);
        Assert.Contains("\"automatic\": false", await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurnedOnAgain_IsOnAtTheNextStart()
    {
        var preferences = Create();

        await preferences.SetAutomaticAsync(false);
        await preferences.SetAutomaticAsync(true);

        Assert.True(Create().IsAutomatic);
    }

    [Fact]
    public async Task ChangesInARow_SaveTheLastOne()
    {
        var preferences = Create();

        await Task.WhenAll(preferences.SetAutomaticAsync(false), preferences.SetAutomaticAsync(true), preferences.SetAutomaticAsync(false));

        Assert.False(Create().IsAutomatic);
    }

    [Theory]
    [InlineData("""{ "automatic": true }""", true)]
    [InlineData("""{ "automatic": false }""", false)]
    [InlineData("""{ "automatic": false, "later": 1 }""", false)]
    public void AFileWithAChoice_IsReadBack(string json, bool automatic)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json);

        Assert.Equal(automatic, Create().IsAutomatic);
        Assert.Empty(_logs.Lines);
    }

    // The user may have turned them off before the file broke (a crash, a full disk): nothing installs without asking.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("{ \"automatic\": fal")]
    [InlineData("\0\0\0\0")]
    [InlineData("null")]
    [InlineData("[false]")]
    [InlineData("{}")]
    [InlineData("""{ "automatic": null }""")]
    [InlineData("""{ "automatic": "no" }""")]
    [InlineData("""{ "automatic": 0 }""")]
    public void AFileThatCannotBeRead_TurnsThemOff_AndSaysSo(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json);

        Assert.False(Create().IsAutomatic);
        Assert.Contains("off until they are switched on", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatCannotBeRead_IsWrittenAgainWhenTheSwitchIsUsed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");
        var preferences = Create();

        await preferences.SetAutomaticAsync(true);

        Assert.True(Create().IsAutomatic);
    }

    [Fact]
    public async Task AFileThatCannotBeWritten_StillTurnsThemOffForThisRun()
    {
        // A directory where the file should be: the save fails.
        Directory.CreateDirectory(FilePath);
        var preferences = Create();

        await preferences.SetAutomaticAsync(false);

        Assert.False(preferences.IsAutomatic);
        Assert.Contains("could not be saved", _logs.All, StringComparison.Ordinal);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}
