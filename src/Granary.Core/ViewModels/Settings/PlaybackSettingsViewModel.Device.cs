using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Settings;

// Settings → Playback → This PC: choices about this PC's hardware and player. They are Granary's own, kept on the PC
// rather than in the profile.
public sealed partial class PlaybackSettingsViewModel
{
    /// <summary>Direct play without full hardware decoding (<see cref="IPlaybackDevicePreferences.DecodesInSoftware"/>).</summary>
    [ObservableProperty]
    public partial bool DecodesInSoftware { get; set; }

    /// <summary>Why mpv.conf or its folder did not open; empty otherwise.</summary>
    [ObservableProperty]
    public partial string MpvConfigMessage { get; private set; } = "";

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
        MpvConfigMessage = "";
        // Off the UI thread: the disk can be slow.
        string? file = await Task.Run(_mpvConfig.EnsureConfigFile);
        if (file is null)
        {
            MpvConfigMessage = _mpvConfig.ConfigFileIsFolder
                ? $"mpv.conf in {_mpvConfig.Directory} is a folder. Rename or remove it, then try again."
                : $"Couldn't create mpv.conf in {_mpvConfig.Directory}.";
        }
        else if (!_files.OpenFile(file))
        {
            MpvConfigMessage = $"Couldn't open mpv.conf. You'll find it in {_mpvConfig.Directory}.";
        }
    }

    /// <summary>Shows the mpv folder, made first when it is missing, for shaders and other files mpv.conf names.</summary>
    [RelayCommand]
    private async Task OpenMpvFolderAsync()
    {
        MpvConfigMessage = "";
        if (!await Task.Run(_mpvConfig.EnsureDirectory) || !_files.OpenFolder(_mpvConfig.Directory))
        {
            MpvConfigMessage = $"Couldn't open {_mpvConfig.Directory}.";
        }
    }
}
