using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Detail;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>A drive new downloads can go to, and its free space when Windows said it.</summary>
internal sealed record DownloadDrive(string Root, long? FreeBytes);

/// <summary>
/// Settings → Downloads, this PC's own choices (<see cref="IDownloadPreferences"/>), each saved as it is made: the folder new
/// downloads go to, with Change and Open, and the PC's drives to choose from, which a controller can do where Windows' folder
/// picker takes none; downloads on metered connections, off by default as both apps have it (Wi-Fi only); the quality the
/// download dialog starts on, labelled as the apps label presets; Keep watched downloads, off by default as both apps have
/// it, which only hides the Downloads page's suggestion to free up space; whether Granary says when downloads finish or
/// fail, on by default; and how many downloads come at once, the Apple app's Simultaneous Downloads (1 to 4, two by
/// default); the Apple app's Monitoring Defaults, which fill in a new monitor (Delete after watching, and a storage limit in
/// its stepper's steps of 5 GB up to 1000); and Granary's own storage limit for downloads, a slider saved once it rests (<see cref="SliderRest"/>). A folder is
/// taken only when Granary can write there. The default quality's line is the Apple app's footer for what the server offers,
/// and Storage says what the profile's downloads use, with the Apple app's Remove All Downloads, which asks first. A folder is
/// taken only when Granary can write there,
/// which is tried off the UI thread, and never a drive's or a share's root.
/// </summary>
public sealed partial class DownloadSettingsViewModel : ObservableObject
{
    public static string FolderRefused => AppText.Get("granary.settings.downloads.folderRefused");

    /// <summary>The file Change writes and deletes in a chosen folder to know downloads can go there.</summary>
    public const string WriteCheckName = ".granary-write-check";

    /// <summary>Where a drive's choice puts new downloads on any drive but the default folder's: <c>D:\Granary\Downloads</c>.</summary>
    public static readonly string DriveFolder = Path.Combine("Granary", "Downloads");

    /// <summary>How long a slider rests before its value is saved: a drag is one save, not one for every step.</summary>
    public static readonly TimeSpan SliderRest = TimeSpan.FromMilliseconds(500);

    /// <summary>The storage limit slider's step, in GB.</summary>
    public const int StorageLimitStepGb = 10;

    // The slider's save, once it rests; on the UI thread.
    private ITimer? _limitSave;

    private readonly IDownloadPreferences _preferences;
    private readonly DownloadsFeature _feature;
    private readonly IFolderPicker _picker;
    private readonly IFileLauncher _launcher;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<DownloadSettingsViewModel> _logger;
    private readonly DownloadManager? _downloads;
    private readonly IConfirmation? _confirmation;

    // Between Attach and Detach: Storage follows the downloads. One refresh is posted to the UI thread at a time.
    private bool _attached;
    private int _storagePosted;

    // Set while a select shows the saved quality or the folder's drive, so showing it saves nothing.
    private bool _showing;

    // Counts the viewer's folder choices (Change, a drive), on the UI thread: a choice whose check ends after a newer one was
    // made changes nothing, so the last choice is the one that holds.
    private int _folderChoices;

    public DownloadSettingsViewModel(IDownloadPreferences preferences, DownloadsFeature feature, IFolderPicker picker, IFileLauncher launcher,
        IUiDispatcher dispatcher, TimeProvider? time = null, ILogger<DownloadSettingsViewModel>? logger = null, DownloadManager? downloads = null,
        IConfirmation? confirmation = null)
    {
        _preferences = preferences;
        _feature = feature;
        _picker = picker;
        _launcher = launcher;
        _dispatcher = dispatcher;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<DownloadSettingsViewModel>.Instance;
        _downloads = downloads;
        _confirmation = confirmation;
        Folder = preferences.Folder;
        DownloadsOnMetered = preferences.DownloadsOnMetered;
        KeepWatched = preferences.KeepWatched;
        NotifyWhenFinished = preferences.NotifyWhenFinished;
        StorageLimitGb = preferences.StorageLimitGb;
        DefaultDeleteWatched = preferences.DefaultDeleteWatched;
        DefaultMonitorLimitGb = preferences.DefaultMonitorLimitGb;
        _showing = true;
        try
        {
            SelectedSimultaneous = SimultaneousChoices.FirstOrDefault(c => c.Value == Count(preferences.SimultaneousDownloads));
        }
        finally
        {
            _showing = false;
        }
        ShowQualities();
        ShowStorage();
    }

    /// <summary>Remove all downloads' question, the Apple app's (<c>downloads.settings.removeAllTitle</c>).</summary>
    public static string RemoveAllTitle => AppText.Get("granary.settings.downloads.removeAllTitle");

    /// <summary>Its yes, the Apple app's (<c>downloads.settings.removeAllConfirm</c>).</summary>
    public static string RemoveAllAction => AppText.Get("granary.settings.downloads.removeAllConfirm");

    /// <summary>Where new downloads go; the ones on this PC stay where they are.</summary>
    [ObservableProperty]
    public partial string Folder { get; private set; } = "";

    /// <summary>Downloads run on a metered connection, such as a phone's hotspot.</summary>
    [ObservableProperty]
    public partial bool DownloadsOnMetered { get; set; }

    /// <summary>Watched downloads are not suggested for deletion on the Downloads page.</summary>
    [ObservableProperty]
    public partial bool KeepWatched { get; set; }

    /// <summary>Granary says when downloads finish or fail: in its window, or as a Windows notification while it is not in front.</summary>
    [ObservableProperty]
    public partial bool NotifyWhenFinished { get; set; }

    /// <summary>
    /// Granary's storage limit for downloads, in GB, 0 for none: the slider's value, saved once it rests. The download dialog
    /// does not start what would pass it, and a monitored series' episodes wait at it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StorageLimitText))]
    public partial double StorageLimitGb { get; set; }

    /// <summary>"No limit", or "250 GB" in the web's size format.</summary>
    public string StorageLimitText => LimitGb(StorageLimitGb) is var gb and > 0
        ? AppText.Get("web.common.format.size.gb", ("size", gb))
        : AppText.Get("granary.settings.downloads.noLimit");

    /// <summary>The highest limit the slider offers, in GB.</summary>
    public double StorageLimitMaxGb => DownloadPreferenceLimits.MaxStorageLimitGb;

    /// <summary>Monitoring Defaults: a new monitor starts with Delete after watching.</summary>
    [ObservableProperty]
    public partial bool DefaultDeleteWatched { get; set; }

    /// <summary>Monitoring Defaults: the storage limit a new monitor starts with, in GB; 0 for none. The stepper's value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultMonitorLimitText))]
    public partial double DefaultMonitorLimitGb { get; set; }

    /// <summary>The Apple app's words: "Storage limit: 25 GB", "Storage limit: Unlimited".</summary>
    public string DefaultMonitorLimitText => MonitorLimitGb(DefaultMonitorLimitGb) is var gb and > 0
        ? AppText.Get("granary.settings.downloads.monitorLimit", ("limit", gb))
        : AppText.Get("granary.settings.downloads.monitorLimitUnlimited");

    /// <summary>The highest limit the stepper offers, in GB.</summary>
    public double DefaultMonitorLimitMaxGb => DownloadPreferenceLimits.MaxMonitorLimitGb;

    /// <summary>The stepper's step, in GB.</summary>
    public double DefaultMonitorLimitStepGb => DownloadPreferenceLimits.MonitorLimitStepGb;

    /// <summary>The Apple app's Simultaneous Downloads: 1 to 4, in the language's digits.</summary>
    public IReadOnlyList<SettingChoice> SimultaneousChoices { get; } =
        [.. Enumerable.Range(DownloadPreferenceLimits.MinSimultaneous, DownloadPreferenceLimits.MaxSimultaneous - DownloadPreferenceLimits.MinSimultaneous + 1).Select(n => new SettingChoice(Count(n), NumberText.Digits(n)))];

    /// <summary>How many downloads come at once.</summary>
    [ObservableProperty]
    public partial SettingChoice? SelectedSimultaneous { get; set; }

    /// <summary>Original and every preset, best first: "Original", "10 Mbps · up to 1080p".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> Qualities { get; private set; } = [];

    /// <summary>The quality the download dialog starts on when the server offers it, else Original.</summary>
    [ObservableProperty]
    public partial SettingChoice? SelectedQuality { get; set; }

    /// <summary>
    /// The Apple app's footer under its quality (<c>downloads.settings.quality*</c>) for what the server offers: what Original
    /// and the presets are, and what the choice is the default for. Its "Download sheet" is the download dialog here.
    /// </summary>
    [ObservableProperty]
    public partial string QualityDescription { get; private set; } = "";

    /// <summary>What the active profile's downloads use on this PC, as the Apple app's Storage → Used says it: "3.0 GB".</summary>
    [ObservableProperty]
    public partial string UsedText { get; private set; } = "";

    /// <summary>The profile has downloads on this PC: Remove All Downloads shows.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveAllCommand))]
    public partial bool CanRemoveAll { get; private set; }

    /// <summary>Why the folder was not taken or did not open; empty otherwise.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>For tests: whether downloads can go to a folder; it runs on the thread pool.</summary>
    internal Func<string, bool> FolderCheck { get; init; } = TakesDownloads;

    /// <summary>
    /// The PC's drives new downloads can go to, with their free space: "D: · 200.0 GB free". Windows' folder picker is a
    /// window of its own, which Granary's controller reading does not reach and which takes no controller itself, so this
    /// list is how a controller chooses where downloads go.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDrives))]
    public partial IReadOnlyList<SettingChoice> Drives { get; private set; } = [];

    /// <summary>The drive the folder is on; none for a share. Choosing another takes a folder there (<see cref="DriveChoice"/>).</summary>
    [ObservableProperty]
    public partial SettingChoice? SelectedDrive { get; set; }

    /// <summary>There is more than one drive to choose from.</summary>
    public bool ShowsDrives => Drives.Count > 1;

    /// <summary>The last drive's choice: done once the folder there was taken or refused.</summary>
    public Task DriveChoice { get; private set; } = Task.CompletedTask;

    /// <summary>For tests: the PC's drives; read on the thread pool.</summary>
    internal Func<IReadOnlyList<DownloadDrive>> DriveList { get; init; } = ReadDrives;

    /// <summary>For tests: the folder downloads go to by default, which its drive's choice takes again.</summary>
    internal string DefaultFolder { get; init; } = DownloadPaths.DefaultRoot;

    /// <summary>
    /// Reads what the server offers this profile, for the labels: a preset's tallest output is the server's own; and the PC's
    /// drives, for <see cref="DiskSpaceReads.Timeout"/> at most, shown once they come, so a drive slow to answer never holds the
    /// qualities back.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var drives = ReadDrivesAsync(cancellationToken);
        await _feature.RefreshAsync(cancellationToken);
        await _dispatcher.ResumeOnUi();
        ShowQualities();
        ShowStorage();
        var listed = await drives;
        await _dispatcher.ResumeOnUi();
        ShowDrives(listed);
    }

    /// <summary>Windows' folder picker; the folder chosen holds new downloads once Granary could write there.</summary>
    [RelayCommand]
    private async Task ChangeFolderAsync()
    {
        Status = "";
        if (await _picker.PickFolderAsync() is not { } picked)
        {
            return;
        }
        int choice = ++_folderChoices;
        if (await TakeFolderAsync(picked, choice))
        {
            ShowDrive();
        }
    }

    // A drive the viewer chose: the default folder on its own drive, else a Granary folder there, once Granary can write there.
    // When it cannot, the choice goes back to the folder's drive. Whatever goes wrong, the select goes back and says so: the
    // task is the select's, which nobody awaits.
    private async Task TakeDriveAsync(string root, int choice)
    {
        Status = "";
        try
        {
            string folder = SameRoot(DefaultFolder, root) ? DefaultFolder : Path.Combine(root, DriveFolder);
            if (!await TakeFolderAsync(folder, choice))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            await _dispatcher.ResumeOnUi();
            _logger.LogWarning("A drive for new downloads could not be taken ({Failure})", ex.GetType().Name);
            if (choice != _folderChoices)
            {
                return;
            }
            Status = FolderRefused;
        }
        ShowDrive();
    }

    // The folder holds new downloads once Granary could write there, which is tried on the thread pool; else the status says it
    // was refused. A drive's root is refused before anything is written there. False when a newer choice came while it was
    // checked (<paramref name="choice"/>): that one says what holds.
    private async Task<bool> TakeFolderAsync(string picked, int choice)
    {
        string? folder = JsonDownloadPreferences.FullFolder(picked);
        var check = FolderCheck;
        bool takes = folder is not null && await Task.Run(() => check(folder));
        await _dispatcher.ResumeOnUi();
        if (choice != _folderChoices)
        {
            return false;
        }
        if (!takes)
        {
            Status = FolderRefused;
            return true;
        }
        try
        {
            // The save never fails; one that cannot be written is logged, and the folder holds for this run.
            _ = _preferences.SetFolderAsync(folder!);
        }
        catch (ArgumentException)
        {
            Status = FolderRefused;
            return true;
        }
        Folder = _preferences.Folder;
        return true;
    }

    /// <summary>Shows the folder in File Explorer, made first when no download has gone there yet.</summary>
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        Status = "";
        string folder = Folder;
        bool made = await Task.Run(() => MakeFolder(folder));
        await _dispatcher.ResumeOnUi();
        if (!made || !_launcher.OpenFolder(folder))
        {
            Status = AppText.Get("granary.settings.downloads.openFailed", ("folder", folder));
        }
    }

    partial void OnDownloadsOnMeteredChanged(bool value)
    {
        if (value != _preferences.DownloadsOnMetered)
        {
            // Never fails: a save that does not work is logged, and the choice holds for this run.
            _ = _preferences.SetDownloadsOnMeteredAsync(value);
        }
    }

    partial void OnKeepWatchedChanged(bool value)
    {
        if (value != _preferences.KeepWatched)
        {
            _ = _preferences.SetKeepWatchedAsync(value);
        }
    }

    partial void OnNotifyWhenFinishedChanged(bool value)
    {
        if (value != _preferences.NotifyWhenFinished)
        {
            _ = _preferences.SetNotifyWhenFinishedAsync(value);
        }
    }

    partial void OnDefaultDeleteWatchedChanged(bool value)
    {
        if (value != _preferences.DefaultDeleteWatched)
        {
            _ = _preferences.SetDefaultDeleteWatchedAsync(value);
        }
    }

    // A stepper's step is saved as it is made; a number typed in is held to the steps and the range first, and shown so.
    partial void OnDefaultMonitorLimitGbChanged(double value)
    {
        int limit = MonitorLimitGb(value);
        if (limit != value)
        {
            DefaultMonitorLimitGb = limit;
            return;
        }
        if (limit != _preferences.DefaultMonitorLimitGb)
        {
            _ = _preferences.SetDefaultMonitorLimitGbAsync(limit);
        }
    }

    private static int MonitorLimitGb(double value) => double.IsFinite(value)
        ? (int)Math.Clamp(Math.Round(value / DownloadPreferenceLimits.MonitorLimitStepGb, MidpointRounding.AwayFromZero) * DownloadPreferenceLimits.MonitorLimitStepGb,
            0, DownloadPreferenceLimits.MaxMonitorLimitGb)
        : 0;

    // A drag moves the value many times: it is saved once the slider rests, as its last value.
    partial void OnStorageLimitGbChanged(double value)
    {
        _limitSave?.Dispose();
        _limitSave = null;
        if (LimitGb(value) == _preferences.StorageLimitGb)
        {
            return;
        }
        _limitSave = _time.CreateTimer(static state => ((DownloadSettingsViewModel)state!).SaveLimit(), this, SliderRest, Timeout.InfiniteTimeSpan);
    }

    // On the timer's thread: the setting's own save goes off it anyway, and its change event is read on any thread.
    private void SaveLimit() => _dispatcher.Post(() =>
    {
        int limit = LimitGb(StorageLimitGb);
        if (limit != _preferences.StorageLimitGb)
        {
            _ = _preferences.SetStorageLimitGbAsync(limit);
        }
    });

    // The slider's value in whole steps, within its range.
    private static int LimitGb(double value) =>
        (int)Math.Clamp(Math.Round(value / StorageLimitStepGb, MidpointRounding.AwayFromZero) * StorageLimitStepGb, 0, DownloadPreferenceLimits.MaxStorageLimitGb);

    partial void OnSelectedSimultaneousChanged(SettingChoice? value)
    {
        if (!_showing && value is not null && int.TryParse(value.Value, CultureInfo.InvariantCulture, out int count) && count != _preferences.SimultaneousDownloads)
        {
            _ = _preferences.SetSimultaneousDownloadsAsync(count);
        }
    }

    partial void OnSelectedQualityChanged(SettingChoice? value)
    {
        if (!_showing && value is not null && value.Value != _preferences.DefaultQuality)
        {
            _ = _preferences.SetDefaultQualityAsync(value.Value);
        }
    }

    // The viewer's pick. One that came back to the folder's drive while another drive was being checked is a choice too: it
    // ends that one, whose check then changes nothing.
    partial void OnSelectedDriveChanged(SettingChoice? value)
    {
        if (_showing || value is null)
        {
            return;
        }
        int choice = ++_folderChoices;
        if (!SameRoot(Folder, value.Value))
        {
            DriveChoice = TakeDriveAsync(value.Value, choice);
        }
    }

    // The drives as their list says them, the folder's chosen; a new list clears the select first, as the qualities' does.
    private void ShowDrives(IReadOnlyList<DownloadDrive> drives)
    {
        _showing = true;
        try
        {
            SelectedDrive = null;
            Drives = [.. drives.Select(d => new SettingChoice(d.Root, DriveLabel(d)))];
        }
        finally
        {
            _showing = false;
        }
        ShowDrive();
    }

    // The folder's drive chosen in the select, as the folder is now.
    private void ShowDrive()
    {
        _showing = true;
        try
        {
            SelectedDrive = Drives.FirstOrDefault(d => SameRoot(Folder, d.Value));
        }
        finally
        {
            _showing = false;
        }
    }

    // "D: · 200.0 GB free"; the letter alone when Windows did not say.
    private static string DriveLabel(DownloadDrive drive)
    {
        string name = drive.Root.TrimEnd('\\', '/');
        return drive.FreeBytes is long free and > 0
            ? AppText.Get("granary.settings.downloads.driveFree", ("drive", name), ("free", PrePlayText.FileSize(free)))
            : name;
    }

    // Whether the folder lies on the drive of that root (letters in any case); a share's folder lies on no drive.
    private static bool SameRoot(string folder, string root) =>
        Path.GetPathRoot(folder) is { Length: > 0 } own && string.Equals(own.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // The drives on the thread pool, for as long as the free space is waited for elsewhere; none when they do not answer in time
    // or cannot be listed, which the log says by its kind.
    private async Task<IReadOnlyList<DownloadDrive>> ReadDrivesAsync(CancellationToken cancellationToken)
    {
        var list = DriveList;
        try
        {
            return await Task.Run(list, CancellationToken.None).WaitAsync(DiskSpaceReads.Timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("The drives for new downloads could not be listed ({Failure})", ex.GetType().Name);
            return [];
        }
    }

    // The drives that hold folders: a disk and a removable drive Windows has ready, with their free space, and a mapped network
    // drive as it is, since asking a share that went away whether it is there can keep Windows waiting for long; not a disc
    // drive. A network drive Granary cannot write to is refused once chosen, as any folder is.
    private static IReadOnlyList<DownloadDrive> ReadDrives()
    {
        var drives = new List<DownloadDrive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Network)
                {
                    drives.Add(new DownloadDrive(drive.Name, null));
                }
                else if ((drive.DriveType is DriveType.Fixed or DriveType.Removable) && drive.IsReady)
                {
                    drives.Add(new DownloadDrive(drive.Name, drive.AvailableFreeSpace));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that went away while it was read is left out.
            }
        }
        return drives;
    }

    /// <summary>
    /// The Apple app's Remove All Downloads: after its question, every download of the profile the question was asked in goes
    /// from this PC, as Delete deletes one; the server hears it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemoveAll))]
    private async Task RemoveAllAsync()
    {
        if (_downloads is null || _confirmation is null || _downloads.ActiveScope is not { } scope)
        {
            return;
        }
        string[] asked = [.. _downloads.Items.Select(i => i.DownloadId)];
        if (asked.Length > 0 && await _confirmation.AskAsync(RemoveAllTitle, AppText.Get("granary.settings.downloads.removeAllMessage"),
            RemoveAllAction))
        {
            // The episodes waiting for the server's limit go first: a delete that reaches the server frees a place, and would
            // start one of them on this PC again. A create of the line under way then keeps nothing, unless its download was kept
            // before: then it is among the downloads read here, and goes too.
            await _downloads.DropWaitingAsync(scope, null, CancellationToken.None);
            string[] now = _downloads.ActiveScope == scope ? [.. _downloads.Items.Select(i => i.DownloadId)] : [];
            await _downloads.DeleteAllAsync(scope, [.. asked.Union(now, StringComparer.Ordinal)], CancellationToken.None);
        }
        await _dispatcher.ResumeOnUi();
        ShowStorage();
    }

    /// <summary>
    /// The view shows, on the UI thread: Storage follows the downloads from now on (one a monitoring run kept, one deleted from
    /// the Downloads page) until <see cref="Detach"/>, which lets go of the downloads so they never hold the view.
    /// </summary>
    public void Attach()
    {
        if (_attached || _downloads is null)
        {
            return;
        }
        _attached = true;
        _downloads.Changed += OnDownloadsChanged;
        ShowStorage();
    }

    /// <summary>The view was left: Storage stops following the downloads.</summary>
    public void Detach()
    {
        if (!_attached || _downloads is null)
        {
            return;
        }
        _attached = false;
        _downloads.Changed -= OnDownloadsChanged;
    }

    // Any thread, as often as a transfer goes further: one refresh at a time is posted to the UI thread.
    private void OnDownloadsChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _storagePosted, 1) == 0)
        {
            _dispatcher.Post(() =>
            {
                Volatile.Write(ref _storagePosted, 0);
                if (_attached)
                {
                    ShowStorage();
                }
            });
        }
    }

    // The space the profile's downloads use, as the queue and the store know it: no disk is read.
    private void ShowStorage()
    {
        if (_downloads is null)
        {
            return;
        }
        long used = _downloads.BytesUsed();
        UsedText = used > 0 ? PrePlayText.FileSize(used) : AppText.Get("web.common.format.size.bytes", ("size", 0));
        CanRemoveAll = _downloads.Items.Count > 0;
    }

    // The Apple app's footer: Original, then the presets (with "at up to the resolution shown" when the labels name one), then
    // what the choice is the default for. Granary asks per episode for a season or a series, so every download takes the
    // presets; only monitors depend on the server (monitor_quality).
    private static string QualityFooter(DownloadCapability? capability)
    {
        bool resolution = capability is null || capability.QualityOptions.Count == 0 || capability.QualityOptions.Any(o => o.MaxHeight is > 0);
        string presets = resolution
            ? AppText.Get("granary.settings.downloads.qualityPresetsResolution")
            : AppText.Get("granary.settings.downloads.qualityPresets");
        string scope = capability?.MonitorQuality == true
            ? AppText.Get("granary.settings.downloads.qualityScopeMonitors")
            : AppText.Get("granary.settings.downloads.qualityScopeDownloads");
        return AppText.Get("granary.settings.downloads.qualityDescription",
            ("original", AppText.Get("granary.settings.downloads.qualityOriginal")), ("presets", presets), ("scope", scope));
    }

    // Each preset as the server offers it, else as the server's guide lists it. A new list clears the select first and then
    // picks the saved quality in it, as a select whose items change needs it.
    private void ShowQualities()
    {
        QualityDescription = QualityFooter(_feature.Capability);
        var offered = _feature.Capability?.QualityOptions ?? [];
        IReadOnlyList<SettingChoice> qualities = [.. DownloadQualities.GuideOptions.Select(guide =>
            new SettingChoice(guide.Preset, DownloadText.QualityLabel(offered.FirstOrDefault(o => o.Preset == guide.Preset) ?? guide)))];
        if (qualities.SequenceEqual(Qualities))
        {
            return;
        }
        _showing = true;
        try
        {
            SelectedQuality = null;
            Qualities = qualities;
            SelectedQuality = qualities.FirstOrDefault(q => q.Value == _preferences.DefaultQuality) ?? qualities[0];
        }
        finally
        {
            _showing = false;
        }
    }

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);

    // The folder can be made, and a file written and deleted in it.
    private static bool TakesDownloads(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string check = Path.Combine(folder, WriteCheckName);
            File.WriteAllBytes(check, [1]);
            File.Delete(check);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static bool MakeFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
