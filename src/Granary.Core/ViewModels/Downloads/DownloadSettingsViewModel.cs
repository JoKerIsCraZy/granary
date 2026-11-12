using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Settings;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// Settings → Downloads, this PC's own choices (<see cref="IDownloadPreferences"/>), each saved as it is made: the folder new
/// downloads go to, with Change and Open; downloads on metered connections, off by default as both apps have it (Wi-Fi only);
/// the quality the download dialog starts on, labelled as the apps label presets; and Keep watched downloads, off by default as
/// both apps have it, which only hides the Downloads page's suggestion to free up space. A folder is taken only when Granary can
/// write there, which is tried off the UI thread, and never a drive's or a share's root.
/// </summary>
public sealed partial class DownloadSettingsViewModel : ObservableObject
{
    public const string FolderRefused = "Granary can't save downloads in that folder. Choose another one.";

    /// <summary>The file Change writes and deletes in a chosen folder to know downloads can go there.</summary>
    public const string WriteCheckName = ".granary-write-check";

    private readonly IDownloadPreferences _preferences;
    private readonly DownloadsFeature _feature;
    private readonly IFolderPicker _picker;
    private readonly IFileLauncher _launcher;
    private readonly IUiDispatcher _dispatcher;

    // Set while the select shows the saved quality, so showing it saves nothing.
    private bool _showing;

    public DownloadSettingsViewModel(IDownloadPreferences preferences, DownloadsFeature feature, IFolderPicker picker, IFileLauncher launcher,
        IUiDispatcher dispatcher)
    {
        _preferences = preferences;
        _feature = feature;
        _picker = picker;
        _launcher = launcher;
        _dispatcher = dispatcher;
        Folder = preferences.Folder;
        DownloadsOnMetered = preferences.DownloadsOnMetered;
        KeepWatched = preferences.KeepWatched;
        ShowQualities();
    }

    /// <summary>Where new downloads go; the ones on this PC stay where they are.</summary>
    [ObservableProperty]
    public partial string Folder { get; private set; } = "";

    /// <summary>Downloads run on a metered connection, such as a phone's hotspot.</summary>
    [ObservableProperty]
    public partial bool DownloadsOnMetered { get; set; }

    /// <summary>Watched downloads are not suggested for deletion on the Downloads page.</summary>
    [ObservableProperty]
    public partial bool KeepWatched { get; set; }

    /// <summary>Original and every preset, best first: "Original", "10 Mbps · up to 1080p".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> Qualities { get; private set; } = [];

    /// <summary>The quality the download dialog starts on when the server offers it, else Original.</summary>
    [ObservableProperty]
    public partial SettingChoice? SelectedQuality { get; set; }

    /// <summary>Why the folder was not taken or did not open; empty otherwise.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>For tests: whether downloads can go to a folder; it runs on the thread pool.</summary>
    internal Func<string, bool> FolderCheck { get; init; } = TakesDownloads;

    /// <summary>Reads what the server offers this profile, for the labels: a preset's tallest output is the server's own.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _feature.RefreshAsync(cancellationToken);
        await _dispatcher.ResumeOnUi();
        ShowQualities();
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
        // A drive's root is refused before anything is written there.
        string? folder = JsonDownloadPreferences.FullFolder(picked);
        var check = FolderCheck;
        bool takes = folder is not null && await Task.Run(() => check(folder));
        await _dispatcher.ResumeOnUi();
        if (!takes)
        {
            Status = FolderRefused;
            return;
        }
        try
        {
            // The save never fails; one that cannot be written is logged, and the folder holds for this run.
            _ = _preferences.SetFolderAsync(folder!);
        }
        catch (ArgumentException)
        {
            Status = FolderRefused;
            return;
        }
        Folder = _preferences.Folder;
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
            Status = $"Couldn't open {folder}.";
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

    partial void OnSelectedQualityChanged(SettingChoice? value)
    {
        if (!_showing && value is not null && value.Value != _preferences.DefaultQuality)
        {
            _ = _preferences.SetDefaultQualityAsync(value.Value);
        }
    }

    // Each preset as the server offers it, else as the server's guide lists it. A new list clears the select first and then
    // picks the saved quality in it, as a select whose items change needs it.
    private void ShowQualities()
    {
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
