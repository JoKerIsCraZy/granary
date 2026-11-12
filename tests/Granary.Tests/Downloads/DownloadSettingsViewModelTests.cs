using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Settings → Downloads: the folder new downloads go to (a folder Granary can write, never a drive's root), downloads on
/// metered connections, and the default quality with the apps' labels.
/// </summary>
public sealed class DownloadSettingsViewModelTests : IAsyncDisposable
{
    private readonly DownloadHarness _h = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly RecordingFileLauncher _launcher = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    private DownloadSettingsViewModel Settings(IDownloadPreferences? preferences = null, IUiDispatcher? dispatcher = null) =>
        new(preferences ?? _h.Preferences, _h.NewFeature(), _picker, _launcher, dispatcher ?? new ImmediateDispatcher());

    [Fact]
    public async Task ChangeFolder_TakesAWritableFolder()
    {
        string picked = Path.Combine(_h.Directory, "Elena's downloads");
        _picker.Picked = picked;
        var settings = Settings();
        Assert.Equal(_h.Root, settings.Folder);

        await settings.ChangeFolderCommand.ExecuteAsync(null);

        Assert.Equal((picked, picked, ""), (settings.Folder, _h.Preferences.Folder, settings.Status));
        Assert.True(Directory.Exists(picked));
        Assert.Empty(Directory.EnumerateFileSystemEntries(picked));
    }

    [Fact]
    public async Task AFolderThatCannotBeWritten_IsRefused()
    {
        Directory.CreateDirectory(_h.Directory);
        string file = Path.Combine(_h.Directory, "not-a-folder");
        await File.WriteAllTextAsync(file, "Elena", Token);
        _picker.Picked = file;
        var settings = Settings();

        await settings.ChangeFolderCommand.ExecuteAsync(null);

        Assert.Equal(DownloadSettingsViewModel.FolderRefused, settings.Status);
        Assert.Equal("Granary can't save downloads in that folder. Choose another one.", settings.Status);
        Assert.Equal((_h.Root, _h.Root), (settings.Folder, _h.Preferences.Folder));

        _picker.Picked = Path.Combine(_h.Directory, "fine");
        await settings.ChangeFolderCommand.ExecuteAsync(null);

        Assert.Equal(("", _picker.Picked), (settings.Status, settings.Folder));
    }

    [Fact]
    public async Task ADrivesRoot_IsRefused()
    {
        _picker.Picked = Path.GetPathRoot(_h.Directory);
        var settings = Settings();

        await settings.ChangeFolderCommand.ExecuteAsync(null);

        Assert.Equal(DownloadSettingsViewModel.FolderRefused, settings.Status);
        Assert.Equal((_h.Root, _h.Root), (settings.Folder, _h.Preferences.Folder));
    }

    [Fact]
    public async Task AFolderTheSettingsRefuse_IsRefused()
    {
        _picker.Picked = Path.Combine(_h.Directory, "fine");
        var refusing = new RefusingPreferences(_h.Root);
        var settings = Settings(refusing);

        await settings.ChangeFolderCommand.ExecuteAsync(null);

        Assert.Equal((DownloadSettingsViewModel.FolderRefused, _h.Root), (settings.Status, settings.Folder));
    }

    [Fact]
    public async Task ACancelledPicker_ChangesNothing()
    {
        var settings = Settings();

        await settings.ChangeFolderCommand.ExecuteAsync(null);

        Assert.Equal(1, _picker.Shown);
        Assert.Equal((_h.Root, ""), (settings.Folder, settings.Status));
    }

    [Fact]
    public async Task TheFolderCheck_RunsOffTheUiThread()
    {
        using var ui = new SerialDispatcher();
        var checkedOnUi = new List<bool>();
        string picked = Path.Combine(_h.Directory, "elsewhere");
        _picker.Picked = picked;
        var settings = new DownloadSettingsViewModel(_h.Preferences, _h.NewFeature(), _picker, _launcher, ui)
        {
            FolderCheck = _ =>
            {
                checkedOnUi.Add(ui.HasThreadAccess);
                return true;
            },
        };

        await ui.RunAsync(() => settings.ChangeFolderCommand.ExecuteAsync(null));

        Assert.Equal([false], checkedOnUi);
        Assert.Equal(picked, ui.Read(() => settings.Folder));
        Assert.Empty(ui.Failures);
    }

    [Fact]
    public void Metered_IsOffByDefault_AndSaves()
    {
        var settings = Settings();
        Assert.False(settings.DownloadsOnMetered);

        settings.DownloadsOnMetered = true;

        Assert.True(_h.Preferences.DownloadsOnMetered);
        Assert.True(Settings().DownloadsOnMetered);
    }

    [Fact]
    public void KeepWatched_IsOffByDefault_AndSaves()
    {
        var settings = Settings();
        Assert.False(settings.KeepWatched);

        settings.KeepWatched = true;

        Assert.True(_h.Preferences.KeepWatched);
        Assert.True(Settings().KeepWatched);
    }

    [Fact]
    public async Task DefaultQuality_ListsOriginalAndThePresets()
    {
        var settings = Settings();

        Assert.Equal(["Original", "20 Mbps · up to 2160p", "10 Mbps · up to 1080p", "5 Mbps · up to 1080p", "2 Mbps · up to 720p", "1 Mbps · up to 480p"],
            settings.Qualities.Select(q => q.Label));
        Assert.Equal(DownloadQualities.Ladder, settings.Qualities.Select(q => q.Value));
        Assert.Equal("original", settings.SelectedQuality?.Value);

        // A server without 4K: its 20 Mbps copies are 1080p at most.
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK,
            DownloadPayloads.Capability(true, true, "original", "20mbps", "10mbps").Replace("\"max_height\": 2160", "\"max_height\": 1080", StringComparison.Ordinal)));
        await settings.LoadAsync(Token);

        Assert.Equal("20 Mbps · up to 1080p", settings.Qualities[1].Label);
        Assert.Equal("5 Mbps · up to 1080p", settings.Qualities[3].Label);
        Assert.Equal("original", settings.SelectedQuality?.Value);
    }

    [Fact]
    public async Task DefaultQuality_Saves_AndStaysChosenAsTheLabelsChange()
    {
        var settings = Settings();

        settings.SelectedQuality = settings.Qualities[3];
        await settings.LoadAsync(Token);

        Assert.Equal("5mbps", _h.Preferences.DefaultQuality);
        Assert.Equal("5mbps", settings.SelectedQuality?.Value);
        Assert.Same(settings.Qualities[3], settings.SelectedQuality);
        Assert.Equal("5mbps", Settings().SelectedQuality?.Value);
    }

    [Fact]
    public async Task OpenFolder_ShowsItInExplorer()
    {
        var settings = Settings();

        await settings.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal(["folder " + settings.Folder], _launcher.Opened);
        Assert.True(Directory.Exists(settings.Folder));
        Assert.Equal("", settings.Status);
    }

    [Fact]
    public async Task AFolderThatDoesNotOpen_SaysSo()
    {
        _launcher.Fails = true;
        var settings = Settings();

        await settings.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal($"Couldn't open {_h.Root}.", settings.Status);
    }

    // Settings that take no folder, as the real ones refuse a path Windows cannot have.
    private sealed class RefusingPreferences(string folder) : IDownloadPreferences
    {
        public string Folder => folder;

        public bool DownloadsOnMetered => false;

        public string DefaultQuality => DownloadQualities.Original;

        public bool KeepWatched => false;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task LatestSave => Task.CompletedTask;

        public Task SetFolderAsync(string folder) => throw new ArgumentException("No folder is taken.", nameof(folder));

        public Task SetDownloadsOnMeteredAsync(bool allowed) => Task.CompletedTask;

        public Task SetDefaultQualityAsync(string quality) => Task.CompletedTask;

        public Task SetKeepWatchedAsync(bool keep) => Task.CompletedTask;
    }
}
