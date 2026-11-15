using Granary.Core;
using Granary.Core.Downloads;
using Granary.Tests.Fakes;

namespace Granary.Tests.Downloads;

/// <summary>This PC's download settings: where new downloads go, metered connections and the default quality, kept across starts.</summary>
public sealed class JsonDownloadPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    private readonly LogSink _logs = new();

    private string FilePath => Path.Combine(_directory, "download-settings.json");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private JsonDownloadPreferences Create() => new(FilePath, _logs.For<JsonDownloadPreferences>());

    [Fact]
    public void TheDefaults_AreTheDataFoldersDownloads_NotOnMetered_Original()
    {
        var preferences = Create();

        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "downloads"), preferences.Folder);
        Assert.Equal(DownloadPaths.DefaultRoot, preferences.Folder);
        Assert.False(preferences.DownloadsOnMetered);
        Assert.Equal("original", preferences.DefaultQuality);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "download-settings.json"), JsonDownloadPreferences.DefaultPath);
    }

    [Fact]
    public async Task KeepWatched_IsOffByDefault_AndStays()
    {
        var preferences = Create();
        Assert.False(preferences.KeepWatched);
        int changes = 0;
        preferences.Changed += (_, _) => changes++;

        await preferences.SetKeepWatchedAsync(true);

        Assert.Equal(1, changes);
        Assert.Contains("\"keep_watched\": true", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
        Assert.True(Create().KeepWatched);
        // A file from before the setting says nothing of it.
        await File.WriteAllTextAsync(FilePath, "{ \"downloads_on_metered\": true }", Token);
        Assert.False(Create().KeepWatched);
    }

    [Fact]
    public async Task SimultaneousDownloads_IsTwoByDefault_OneToFour_AndStays()
    {
        var preferences = Create();
        Assert.Equal(2, preferences.SimultaneousDownloads);

        await preferences.SetSimultaneousDownloadsAsync(4);

        Assert.Contains("\"simultaneous_downloads\": 4", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
        Assert.Equal(4, Create().SimultaneousDownloads);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => preferences.SetSimultaneousDownloadsAsync(5));
        // A file someone wrote by hand is held to the choices, as the Apple app holds its setting.
        await File.WriteAllTextAsync(FilePath, "{ \"simultaneous_downloads\": 9 }", Token);
        Assert.Equal(4, Create().SimultaneousDownloads);
        await File.WriteAllTextAsync(FilePath, "{ \"simultaneous_downloads\": 0 }", Token);
        Assert.Equal(1, Create().SimultaneousDownloads);
    }

    [Fact]
    public async Task TheMonitoringDefaults_AreKeepWatchedAndUnlimited_AndStay()
    {
        var preferences = Create();
        Assert.Equal((false, 0), (preferences.DefaultDeleteWatched, preferences.DefaultMonitorLimitGb));

        await preferences.SetDefaultDeleteWatchedAsync(true);
        await preferences.SetDefaultMonitorLimitGbAsync(25);

        string file = await File.ReadAllTextAsync(FilePath, Token);
        Assert.Contains("\"default_delete_watched\": true", file, StringComparison.Ordinal);
        Assert.Contains("\"default_monitor_limit_gb\": 25", file, StringComparison.Ordinal);
        Assert.Equal((true, 25), (Create().DefaultDeleteWatched, Create().DefaultMonitorLimitGb));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => preferences.SetDefaultMonitorLimitGbAsync(1001));
        await File.WriteAllTextAsync(FilePath, "{ \"default_monitor_limit_gb\": 5000 }", Token);
        Assert.Equal(1000, Create().DefaultMonitorLimitGb);
    }

    [Fact]
    public async Task TheStorageLimitForDownloads_IsOffByDefault_AndStays()
    {
        var preferences = Create();
        Assert.Equal(0, preferences.StorageLimitGb);

        await preferences.SetStorageLimitGbAsync(250);

        Assert.Contains("\"storage_limit_gb\": 250", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
        Assert.Equal(250, Create().StorageLimitGb);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => preferences.SetStorageLimitGbAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => preferences.SetStorageLimitGbAsync(DownloadPreferenceLimits.MaxStorageLimitGb + 1));
        await File.WriteAllTextAsync(FilePath, "{ \"storage_limit_gb\": -5 }", Token);
        Assert.Equal(0, Create().StorageLimitGb);
    }

    [Fact]
    public async Task NotifyWhenFinished_IsOnByDefault_AlsoForAFileFromBefore_AndStays()
    {
        var preferences = Create();
        Assert.True(preferences.NotifyWhenFinished);
        int changes = 0;
        preferences.Changed += (_, _) => changes++;

        await preferences.SetNotifyWhenFinishedAsync(false);

        Assert.Equal(1, changes);
        Assert.Contains("\"notify_when_finished\": false", await File.ReadAllTextAsync(FilePath, Token), StringComparison.Ordinal);
        Assert.False(Create().NotifyWhenFinished);
        // A file from before the setting says nothing of it: the messages are on.
        await File.WriteAllTextAsync(FilePath, "{ \"keep_watched\": true }", Token);
        Assert.True(Create().NotifyWhenFinished);
    }

    [Fact]
    public async Task Changes_StayAtTheNextStart()
    {
        var preferences = Create();
        string folder = Path.Combine(_directory, "D", "Granary downloads");
        int changes = 0;
        preferences.Changed += (_, _) => changes++;

        await preferences.SetDownloadsOnMeteredAsync(true);
        await preferences.SetFolderAsync(folder);
        await preferences.SetDefaultQualityAsync("5mbps");

        Assert.Equal(3, changes);
        string text = await File.ReadAllTextAsync(FilePath, Token);
        Assert.Contains("\"downloads_on_metered\": true", text, StringComparison.Ordinal);
        Assert.Contains("\"default_quality\": \"5mbps\"", text, StringComparison.Ordinal);
        var again = Create();
        Assert.Equal((folder, true, "5mbps"), (again.Folder, again.DownloadsOnMetered, again.DefaultQuality));
    }

    [Fact]
    public async Task AFileThatCannotBeRead_GivesTheDefaults()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, """{ "folder": "relative\\path", "default_quality": "99mbps", "downloads_on_metered": """, Token);

        var preferences = Create();

        Assert.Equal((DownloadPaths.DefaultRoot, false, "original"), (preferences.Folder, preferences.DownloadsOnMetered, preferences.DefaultQuality));
    }

    [Fact]
    public async Task ValuesItDoesNotKnow_AreTheDefaults()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, """{ "folder": "relative\\path", "default_quality": "99mbps", "downloads_on_metered": true }""", Token);

        var preferences = Create();

        Assert.Equal((DownloadPaths.DefaultRoot, true, "original"), (preferences.Folder, preferences.DownloadsOnMetered, preferences.DefaultQuality));
    }

    [Fact]
    public async Task AFolderThatIsNoPath_IsTheDefault()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, """{ "folder": "C:\\bad\u0000path", "downloads_on_metered": true }""", Token);

        var preferences = Create();

        Assert.Equal((DownloadPaths.DefaultRoot, true), (preferences.Folder, preferences.DownloadsOnMetered));
    }

    [Theory]
    [InlineData("C:\\")]
    [InlineData("D:")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\server\\share\\")]
    [InlineData("C:\\a:b")]
    [InlineData("downloads")]
    public async Task AFolderThatIsADriveARootOrNoPath_IsRefused(string folder)
    {
        var preferences = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => preferences.SetFolderAsync(folder));

        Assert.Equal(DownloadPaths.DefaultRoot, preferences.Folder);
    }

    [Fact]
    public async Task ADriveRootInTheFile_IsTheDefault()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, """{ "folder": "C:\\" }""", Token);

        Assert.Equal(DownloadPaths.DefaultRoot, Create().Folder);
    }

    [Fact]
    public async Task ASaveThatFails_KeepsTheChoiceForThisRun()
    {
        // A folder where the file belongs: the move onto it fails.
        Directory.CreateDirectory(FilePath);
        var preferences = Create();

        await preferences.SetDownloadsOnMeteredAsync(true);

        Assert.True(preferences.DownloadsOnMetered);
        Assert.Contains("could not be saved", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain(_directory, _logs.All, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([FilePath], Directory.GetFileSystemEntries(_directory));
    }
}
