using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Diagnostics;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Settings;

// Settings → Playback → This PC: choices about this PC's hardware and player. They are Granary's own, kept on the PC
// rather than in the profile.
public sealed partial class PlaybackSettingsViewModel
{
    /// <summary>Direct play without full hardware decoding (<see cref="IPlaybackDevicePreferences.DecodesInSoftware"/>).</summary>
    [ObservableProperty]
    public partial bool DecodesInSoftware { get; set; }

    // Each press of Open mpv.conf or Open folder; the latest says how it went.
    private int _mpvConfigRequests;

    /// <summary>Why mpv.conf or its folder did not open; empty otherwise.</summary>
    [ObservableProperty]
    public partial string MpvConfigMessage { get; private set; } = "";

    /// <summary>
    /// How your mpv.conf fared in this run, as the player reported it (<see cref="IMpvConfigStatus"/>): not used, dropped
    /// while a title played, a change a new mpv did not start for, or used with options Granary kept as its own. Empty when
    /// it was used as written, when there is none, or before the player first started.
    /// </summary>
    [ObservableProperty]
    public partial string MpvConfigStatusText { get; private set; } = "";

    /// <summary>Up and Down in the player (<see cref="IPlaybackDevicePreferences.UpDown"/>), the TV app's way first.</summary>
    public IReadOnlyList<SettingChoice> UpDownChoices { get; } =
        [new("controls", "Controls and menus"), new("volume", "Volume")];

    [ObservableProperty]
    public partial SettingChoice? SelectedUpDown { get; set; }

    partial void OnSelectedUpDownChanged(SettingChoice? value)
    {
        var upDown = value?.Value == "volume" ? PlayerUpDown.Volume : PlayerUpDown.ControlsAndMenus;
        if (value is not null && upDown != _devicePreferences.UpDown)
        {
            // Never fails, as the switch below; the player reads it at its next key.
            _ = _devicePreferences.SetUpDownAsync(upDown);
        }
    }

    private const string CustomChoice = "custom";

    /// <summary>The still watching prompt (<see cref="IPlaybackDevicePreferences.PassoutThreshold"/>): the named numbers, then Custom.</summary>
    public IReadOnlyList<SettingChoice> StillWatchingChoices { get; } =
    [
        .. AutoPlayStreak.ThresholdPresets.Select(episodes =>
            new SettingChoice(SettingValues.Invariant(episodes), episodes == 0 ? "Unlimited" : $"{SettingValues.Invariant(episodes)} episodes")),
        new SettingChoice(CustomChoice, "Custom"),
    ];

    [ObservableProperty]
    public partial SettingChoice? SelectedStillWatching { get; set; }

    /// <summary>Custom is chosen, so the number shows.</summary>
    [ObservableProperty]
    public partial bool IsStillWatchingCustom { get; private set; }

    /// <summary>Custom's number. A double, as the number box has it: cleared, it is not a number.</summary>
    [ObservableProperty]
    public partial double StillWatchingEpisodes { get; set; }

    // What the page opens with: the number by its name if it has one, else Custom. Nothing is saved by showing it.
    private void ShowStillWatching(int episodes)
    {
        StillWatchingEpisodes = episodes > 0 ? episodes : AutoPlayStreak.DefaultThreshold;
        SelectedStillWatching = StillWatchingChoices.FirstOrDefault(c => c.Value == SettingValues.Invariant(episodes)) ?? StillWatchingChoices[^1];
    }

    partial void OnSelectedStillWatchingChanged(SettingChoice? value)
    {
        if (value is null)
        {
            return;
        }
        IsStillWatchingCustom = value.Value == CustomChoice;
        if (!IsStillWatchingCustom)
        {
            SaveStillWatching(int.Parse(value.Value, CultureInfo.InvariantCulture));
            return;
        }
        // Custom starts from what holds; from Unlimited, whose number is no use to start from, at the default.
        int holds = _devicePreferences.PassoutThreshold;
        StillWatchingEpisodes = holds > 0 ? holds : AutoPlayStreak.DefaultThreshold;
        SaveStillWatching((int)StillWatchingEpisodes);
    }

    partial void OnStillWatchingEpisodesChanged(double value)
    {
        if (double.IsNaN(value))
        {
            // The box was cleared: it shows what holds again.
            int holds = _devicePreferences.PassoutThreshold;
            StillWatchingEpisodes = holds > 0 ? holds : AutoPlayStreak.DefaultThreshold;
            return;
        }
        int episodes = (int)Math.Clamp(Math.Round(value), 1, AutoPlayStreak.MaxThreshold);
        if (episodes != value)
        {
            StillWatchingEpisodes = episodes;
        }
        else if (IsStillWatchingCustom)
        {
            SaveStillWatching(episodes);
        }
    }

    private void SaveStillWatching(int episodes)
    {
        if (episodes != _devicePreferences.PassoutThreshold)
        {
            // Never fails, as the switch below; a change counts from the next episode.
            _ = _devicePreferences.SetPassoutThresholdAsync(episodes);
        }
    }

    partial void OnDecodesInSoftwareChanged(bool value)
    {
        if (value != _devicePreferences.DecodesInSoftware)
        {
            // Never fails: a save that does not work is logged, and the choice holds for this run.
            _ = _devicePreferences.SetDecodesInSoftwareAsync(value);
        }
    }

    /// <summary>Opens the viewer's own mpv.conf in an editor, made first with a few comments when there is none.</summary>
    [RelayCommand]
    private async Task OpenMpvConfigAsync()
    {
        int request = StartMpvConfigRequest();
        // Off the UI thread: the disk can be slow.
        string? file = await Task.Run(_mpvConfig.EnsureConfigFile);
        string message = file is null
            ? _mpvConfig.ConfigFileIsFolder
                ? $"mpv.conf in {_mpvConfig.Directory} is a folder. Rename or remove it, then try again."
                : $"Couldn't create mpv.conf in {_mpvConfig.Directory}."
            : _files.OpenFile(file) ? "" : $"Couldn't open mpv.conf. You'll find it in {_mpvConfig.Directory}.";
        SayMpvConfig(request, message);
    }

    /// <summary>Shows the mpv folder, made first when it is missing, for shaders and other files mpv.conf names.</summary>
    [RelayCommand]
    private async Task OpenMpvFolderAsync()
    {
        int request = StartMpvConfigRequest();
        bool opened = await Task.Run(_mpvConfig.EnsureDirectory) && _files.OpenFolder(_mpvConfig.Directory);
        SayMpvConfig(request, opened ? "" : $"Couldn't open {_mpvConfig.Directory}.");
    }

    /// <summary>Shows Granary's log folder, which names what the player did with mpv.conf; the data folder before the first log.</summary>
    [RelayCommand]
    private void OpenLogs()
    {
        int request = StartMpvConfigRequest();
        bool opened = _files.OpenFolder(FileLoggerProvider.DefaultDirectory) || _files.OpenFolder(AppPaths.DataDirectory);
        SayMpvConfig(request, opened ? "" : $"Couldn't open {FileLoggerProvider.DefaultDirectory}.");
    }

    internal static string StatusText(MpvConfigState state) => state.Use switch
    {
        MpvConfigUse.NotUsed => $"Your mpv.conf was not used: {state.Reason}. The log has the details.",
        MpvConfigUse.StoppedDuringPlayback => $"Granary stopped using your mpv.conf: {state.Reason}. The log has the details.",
        MpvConfigUse.NotApplied => "Couldn't apply your mpv.conf. Granary keeps playing with the one it started with. The log has the details.",
        MpvConfigUse.Used when state.KeptOptions > 0 =>
            $"Granary kept {state.KeptOptions} of its own options from your mpv.conf. The log names them.",
        _ => "",
    };

    private void ReadMpvConfigStatus() => MpvConfigStatusText = StatusText(_mpvStatus.Current);

    // On the UI thread (MpvConfigChanges).
    private void OnMpvChanged(object? sender, EventArgs e) => ReadMpvConfigStatus();

    /// <summary>The page left: it stops following how the file fares.</summary>
    public void Dispose() => _mpvChanges.Changed -= OnMpvChanged;

    // The buttons share one line: the last one pressed says how it went, and one that ends after it says nothing.
    private int StartMpvConfigRequest()
    {
        MpvConfigMessage = "";
        return Interlocked.Increment(ref _mpvConfigRequests);
    }

    private void SayMpvConfig(int request, string message)
    {
        if (request == Volatile.Read(ref _mpvConfigRequests))
        {
            MpvConfigMessage = message;
        }
    }
}
