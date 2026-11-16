using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// Settings → Downloads: the folder new downloads go to (a folder Granary can write, never a drive's root), also by drive for
/// a controller, downloads on metered connections, and the default quality with the apps' labels.
/// </summary>
public sealed class DownloadSettingsViewModelTests : IAsyncDisposable
{
    private const long GB = 1024L * 1024 * 1024;

    private readonly DownloadHarness _h = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly RecordingFileLauncher _launcher = new();

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    // This PC's own drives are never read: the tests that list drives give their own.
    private DownloadSettingsViewModel Settings(IDownloadPreferences? preferences = null, IUiDispatcher? dispatcher = null) =>
        new(preferences ?? _h.Preferences, _h.NewFeature(), _picker, _launcher, dispatcher ?? new ImmediateDispatcher(), _h.Time) { DriveList = () => [] };

    [Fact]
    public void SimultaneousDownloads_OffersOneToFour_AndSavesTheChoice()
    {
        var settings = Settings();

        Assert.Equal(["1", "2", "3", "4"], settings.SimultaneousChoices.Select(c => c.Label));
        Assert.Equal("2", settings.SelectedSimultaneous?.Value);

        settings.SelectedSimultaneous = settings.SimultaneousChoices[2];

        Assert.Equal(3, _h.Preferences.SimultaneousDownloads);
    }

    [Fact]
    public async Task TheDefaultQualitysLine_IsTheAppleAppsFooter_ForWhatTheServerOffers()
    {
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Capability()));
        var settings = Settings();
        await settings.LoadAsync(Token);

        Assert.Equal("Original prefers source quality and may prepare a compatibility file if this device needs one. Bitrate presets are "
            + "prepared on the server when the original is larger, at up to the resolution shown. This is the default for every download and "
            + "monitor; the download dialog can change it.", settings.QualityDescription);

        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Capability(monitorQuality: false)));
        var originals = Settings();
        await originals.LoadAsync(Token);

        Assert.EndsWith("This is the default for every download; the download dialog can change it. Monitored series download in original quality.",
            originals.QualityDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Storage_SaysWhatTheDownloadsUse_AndRemoveAllDeletesThemAfterAsking()
    {
        var confirmation = new FakeConfirmation { Answer = false };
        await _h.AddAsync("dl_1", DownloadState.Completed, size: 2 * GB, mediaItemId: "mv_1");
        await _h.AddAsync("dl_2", DownloadState.Completed, size: GB, mediaItemId: "mv_2");
        var settings = new DownloadSettingsViewModel(_h.Preferences, _h.NewFeature(), _picker, _launcher, new ImmediateDispatcher(), _h.Time,
            downloads: _h.NewManager(), confirmation: confirmation) { DriveList = () => [] };
        await settings.LoadAsync(Token);

        Assert.Equal(("3.0 GB", true), (settings.UsedText, settings.CanRemoveAll));

        await settings.RemoveAllCommand.ExecuteAsync(null);
        Assert.Equal(("Remove all downloaded files?", "Remove all"), (confirmation.Asked[0].Title, confirmation.Asked[0].Action));
        Assert.Equal(2, _h.Store.In(Scope7).Count);

        confirmation.Answer = true;
        await settings.RemoveAllCommand.ExecuteAsync(null);

        Assert.Empty(_h.Store.In(Scope7));
        Assert.Equal(("0 B", false), (settings.UsedText, settings.CanRemoveAll));
    }

    // Remove All leaves nothing to come back: the episodes waiting for the server's limit of downloads go first.
    [Fact]
    public async Task RemoveAll_AlsoDropsTheEpisodesWaitingForTheServersLimit()
    {
        _h.List();
        _h.Silo.On(HttpMethod.Post, "api/v2/downloads", ScriptedSilo.Problem(429, ProblemCodes.RateLimited));
        await _h.AddAsync("dl_1", DownloadState.Completed, mediaItemId: "mv_1");
        await _h.Store.AddWaitingUnlessClearedAsync([new WaitingEpisode(Scope7, "sr_1", "Example Series", 1, "ep_2", 2, "Chapter Two", null, "original")],
            _h.Store.ClearGeneration, Token);
        var manager = _h.NewManager();
        await manager.WaitingRun;
        var settings = new DownloadSettingsViewModel(_h.Preferences, _h.NewFeature(), _picker, _launcher, new ImmediateDispatcher(), _h.Time,
            downloads: manager, confirmation: new FakeConfirmation()) { DriveList = () => [] };
        await settings.LoadAsync(Token);

        await settings.RemoveAllCommand.ExecuteAsync(null);

        Assert.Empty(_h.Store.WaitingIn(Scope7));
        Assert.Empty(_h.Store.In(Scope7));
    }

    // CodeRabbit on #171: a profile whose first episode the limit refused has nothing downloaded yet, only a line: Remove all
    // downloads is offered all the same, and drops it.
    [Fact]
    public async Task RemoveAll_IsOfferedForWaitingEpisodesAlone_AndDropsThem()
    {
        _h.List();
        _h.Silo.On(HttpMethod.Post, "api/v2/downloads", ScriptedSilo.Problem(429, ProblemCodes.RateLimited));
        await _h.Store.LoadAsync(Token);
        await _h.Store.AddWaitingUnlessClearedAsync([new WaitingEpisode(Scope7, "sr_1", "Example Series", 1, "ep_2", 2, "Chapter Two", null, "original")],
            _h.Store.ClearGeneration, Token);
        var manager = _h.NewManager();
        await manager.WaitingRun;
        var confirmation = new FakeConfirmation();
        var settings = new DownloadSettingsViewModel(_h.Preferences, _h.NewFeature(), _picker, _launcher, new ImmediateDispatcher(), _h.Time,
            downloads: manager, confirmation: confirmation) { DriveList = () => [] };
        await settings.LoadAsync(Token);
        Assert.True(settings.CanRemoveAll);

        await settings.RemoveAllCommand.ExecuteAsync(null);

        Assert.Single(confirmation.Asked);
        Assert.Empty(_h.Store.WaitingIn(Scope7));
        Assert.False(settings.CanRemoveAll);
    }

    [Fact]
    public async Task Storage_FollowsTheDownloadsWhileItShows_AndNoLongerOnceItIsLeft()
    {
        var settings = new DownloadSettingsViewModel(_h.Preferences, _h.NewFeature(), _picker, _launcher, new ImmediateDispatcher(), _h.Time,
            downloads: _h.NewManager(), confirmation: new FakeConfirmation()) { DriveList = () => [] };
        settings.Attach();
        Assert.Equal(("0 B", false), (settings.UsedText, settings.CanRemoveAll));

        // A monitoring run kept the first download while Settings showed.
        await _h.AddAsync("dl_1", DownloadState.Completed, size: GB, mediaItemId: "mv_1");

        Assert.Equal(("1.0 GB", true), (settings.UsedText, settings.CanRemoveAll));

        settings.Detach();
        await _h.AddAsync("dl_2", DownloadState.Completed, size: GB, mediaItemId: "mv_2");

        Assert.Equal("1.0 GB", settings.UsedText);
    }

    [Fact]
    public void MonitoringDefaults_SaveDeleteAfterWatching_AndAStorageLimitInStepsOfFive()
    {
        var settings = Settings();
        Assert.Equal((false, 0d, "Storage limit: Unlimited"), (settings.DefaultDeleteWatched, settings.DefaultMonitorLimitGb, settings.DefaultMonitorLimitText));

        settings.DefaultDeleteWatched = true;
        settings.DefaultMonitorLimitGb = 25;

        Assert.Equal((true, 25), (_h.Preferences.DefaultDeleteWatched, _h.Preferences.DefaultMonitorLimitGb));
        Assert.Equal("Storage limit: 25 GB", settings.DefaultMonitorLimitText);

        // A number typed in is held to the stepper's steps and range.
        settings.DefaultMonitorLimitGb = 23;
        Assert.Equal(25, _h.Preferences.DefaultMonitorLimitGb);
        settings.DefaultMonitorLimitGb = 4000;
        Assert.Equal(1000, _h.Preferences.DefaultMonitorLimitGb);
    }

    [Fact]
    public void TheStorageLimitForDownloads_SaysItsLimit_AndIsSavedOnceTheSliderRests()
    {
        var settings = Settings();
        Assert.Equal((0d, "No limit"), (settings.StorageLimitGb, settings.StorageLimitText));

        settings.StorageLimitGb = 120;
        settings.StorageLimitGb = 250;

        Assert.Equal("250 GB", settings.StorageLimitText);
        Assert.Equal(0, _h.Preferences.StorageLimitGb);
        _h.Time.Advance(DownloadSettingsViewModel.SliderRest);
        Assert.Equal(250, _h.Preferences.StorageLimitGb);
    }

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

    // Windows' folder picker takes no controller, so the drives come as a list a controller can pick from as well.
    private const string DefaultFolder = @"C:\Users\Elena\AppData\Local\Granary\downloads";

    private DownloadSettingsViewModel WithDrives(List<string>? checkedFolders = null, Func<string, bool>? check = null) =>
        new(_h.Preferences, _h.NewFeature(), _picker, _launcher, new ImmediateDispatcher())
        {
            DriveList = () => [new(@"C:\", 120 * GB), new(@"D:\", 2 * GB), new(@"E:\", null)],
            DefaultFolder = DefaultFolder,
            FolderCheck = folder =>
            {
                checkedFolders?.Add(folder);
                return check?.Invoke(folder) ?? true;
            },
        };

    [Fact]
    public async Task InGerman_ThePageSpeaksTheAppleAppsWords_AndItsOwnWithGermanNumbers()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Capability()));
        await _h.Preferences.SetFolderAsync(@"D:\Media\Downloads");
        var settings = WithDrives();
        await settings.LoadAsync(Token);

        Assert.Equal("Original bevorzugt die Qualität der Quelle und bereitet eventuell eine kompatible Datei vor, falls dieses Gerät eine "
            + "benötigt. Bitraten-Voreinstellungen werden auf dem Server vorbereitet, wenn das Original größer ist, und zwar bis zur angezeigten "
            + "Auflösung. Das ist die Standardeinstellung für jeden Download und jede Überwachung; im Download-Fenster kannst du sie ändern.",
            settings.QualityDescription);
        Assert.Equal(["C: · 120,0 GB frei", "D: · 2,0 GB frei", "E:"], settings.Drives.Select(d => d.Label));

        var limits = Settings();
        Assert.Equal(("Kein Limit", "Speicherlimit: Unbegrenzt"), (limits.StorageLimitText, limits.DefaultMonitorLimitText));

        limits.StorageLimitGb = 250;
        limits.DefaultMonitorLimitGb = 25;

        Assert.Equal(("250 GB", "Speicherlimit: 25 GB"), (limits.StorageLimitText, limits.DefaultMonitorLimitText));
        Assert.Equal("Granary kann in diesem Ordner keine Downloads speichern. Wähle einen anderen.", DownloadSettingsViewModel.FolderRefused);
    }

    [Fact]
    public async Task TheDrives_AreListedWithTheirFreeSpace_TheFoldersOwnChosen()
    {
        await _h.Preferences.SetFolderAsync(@"D:\Media\Downloads");
        var settings = WithDrives();

        await settings.LoadAsync(Token);

        Assert.True(settings.ShowsDrives);
        Assert.Equal(["C: · 120.0 GB free", "D: · 2.0 GB free", "E:"], settings.Drives.Select(d => d.Label));
        Assert.Equal(@"D:\", settings.SelectedDrive?.Value);
    }

    [Fact]
    public async Task ChoosingADrive_PutsNewDownloadsInAGranaryFolderThere_TheDefaultFoldersDriveItsDefaultFolder()
    {
        await _h.Preferences.SetFolderAsync(@"D:\Media\Downloads");
        var checkedFolders = new List<string>();
        var settings = WithDrives(checkedFolders);
        await settings.LoadAsync(Token);

        settings.SelectedDrive = settings.Drives[2];
        await settings.DriveChoice;

        Assert.Equal((@"E:\Granary\Downloads", @"E:\Granary\Downloads", ""), (settings.Folder, _h.Preferences.Folder, settings.Status));

        settings.SelectedDrive = settings.Drives[0];
        await settings.DriveChoice;

        Assert.Equal((DefaultFolder, @"C:\"), (_h.Preferences.Folder, settings.SelectedDrive?.Value));
        Assert.Equal([@"E:\Granary\Downloads", DefaultFolder], checkedFolders);
    }

    [Fact]
    public async Task ADriveThatTakesNoDownloads_IsRefused_AndTheChoiceGoesBack()
    {
        await _h.Preferences.SetFolderAsync(@"D:\Media\Downloads");
        var settings = WithDrives(check: _ => false);
        await settings.LoadAsync(Token);

        settings.SelectedDrive = settings.Drives[2];
        await settings.DriveChoice;

        Assert.Equal(DownloadSettingsViewModel.FolderRefused, settings.Status);
        Assert.Equal((@"D:\Media\Downloads", @"D:\"), (_h.Preferences.Folder, settings.SelectedDrive?.Value));
    }

    // A drive slow to take the check (a network drive) loses to the viewer's next choice, made meanwhile: the last one holds.
    [Fact]
    public async Task ADrivesCheckThatEndsAfterANewerChoice_ChangesNothing()
    {
        await _h.Preferences.SetFolderAsync(@"D:\Media\Downloads");
        using var slow = new ManualResetEventSlim();
        var settings = WithDrives(check: folder =>
        {
            if (folder.StartsWith(@"E:\", StringComparison.Ordinal))
            {
                slow.Wait(Token);
            }
            return true;
        });
        await settings.LoadAsync(Token);

        settings.SelectedDrive = settings.Drives[2];
        var first = settings.DriveChoice;
        settings.SelectedDrive = settings.Drives[0];
        await settings.DriveChoice;
        slow.Set();
        await first;

        Assert.Equal((DefaultFolder, DefaultFolder, @"C:\"), (settings.Folder, _h.Preferences.Folder, settings.SelectedDrive?.Value));

        // Back to the folder's own drive while another one is checked: that one ends it too.
        slow.Reset();
        settings.SelectedDrive = settings.Drives[2];
        first = settings.DriveChoice;
        settings.SelectedDrive = settings.Drives[0];
        slow.Set();
        await first;

        Assert.Equal((DefaultFolder, @"C:\", ""), (_h.Preferences.Folder, settings.SelectedDrive?.Value, settings.Status));
    }

    [Fact]
    public async Task DrivesThatDoNotAnswerInTime_LeaveTheChoiceOut_AndHoldNoQualityBack()
    {
        using var stuck = new ManualResetEventSlim();
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK,
            DownloadPayloads.Capability(true, true, "original", "20mbps").Replace("\"max_height\": 2160", "\"max_height\": 1080", StringComparison.Ordinal)));
        var settings = new DownloadSettingsViewModel(_h.Preferences, _h.NewFeature(), _picker, _launcher, new ImmediateDispatcher(), _h.Time,
            _h.Logs.For<DownloadSettingsViewModel>())
        {
            DriveList = () =>
            {
                stuck.Wait(Token);
                return [new(@"C:\", GB), new(@"D:\", GB)];
            },
        };
        try
        {
            var load = settings.LoadAsync(Token);
            await Until(() => settings.Qualities[1].Label == "20 Mbps · up to 1080p", "the qualities showed while the drives were read");
            Assert.False(load.IsCompleted);

            _h.Time.Advance(DiskSpaceReads.Timeout);
            await load;

            Assert.False(settings.ShowsDrives);
            Assert.Contains(_h.Logs.Lines, line => line.Contains("could not be listed (TimeoutException)", StringComparison.Ordinal));
        }
        finally
        {
            stuck.Set();
        }
    }

    [Fact]
    public async Task DrivesThatCannotBeListed_LeaveTheChoiceOut()
    {
        var settings = new DownloadSettingsViewModel(_h.Preferences, _h.NewFeature(), _picker, _launcher, new ImmediateDispatcher(), _h.Time,
            _h.Logs.For<DownloadSettingsViewModel>())
        {
            DriveList = () => throw new InvalidOperationException("Windows said no."),
        };

        await settings.LoadAsync(Token);

        Assert.False(settings.ShowsDrives);
        Assert.Contains(_h.Logs.Lines, line => line.Contains("could not be listed (InvalidOperationException)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneDrive_OffersNoChoice_AndAFolderChangedElsewhereMovesTheChoice()
    {
        var single = new DownloadSettingsViewModel(_h.Preferences, _h.NewFeature(), _picker, _launcher, new ImmediateDispatcher())
        {
            DriveList = () => [new(@"C:\", 120 * GB)],
        };
        await single.LoadAsync(Token);
        Assert.False(single.ShowsDrives);

        await _h.Preferences.SetFolderAsync(@"D:\Media\Downloads");
        var settings = WithDrives();
        await settings.LoadAsync(Token);
        _picker.Picked = @"E:\Elena\Downloads";

        await settings.ChangeFolderCommand.ExecuteAsync(null);

        Assert.Equal(@"E:\", settings.SelectedDrive?.Value);
    }

    [Fact]
    public void NotifyWhenFinished_IsOnByDefault_AndSaves()
    {
        var settings = Settings();
        Assert.True(settings.NotifyWhenFinished);

        settings.NotifyWhenFinished = false;

        Assert.False(_h.Preferences.NotifyWhenFinished);
        Assert.False(Settings().NotifyWhenFinished);
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

        public bool NotifyWhenFinished => true;

        public int SimultaneousDownloads => 2;

        public int StorageLimitGb => 0;

        public bool DefaultDeleteWatched => false;

        public int DefaultMonitorLimitGb => 0;

        public Task SetDefaultDeleteWatchedAsync(bool delete) => Task.CompletedTask;

        public Task SetDefaultMonitorLimitGbAsync(int gigabytes) => Task.CompletedTask;

        public Task SetStorageLimitGbAsync(int gigabytes) => Task.CompletedTask;

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

        public Task SetNotifyWhenFinishedAsync(bool notify) => Task.CompletedTask;

        public Task SetSimultaneousDownloadsAsync(int count) => Task.CompletedTask;
    }
}
