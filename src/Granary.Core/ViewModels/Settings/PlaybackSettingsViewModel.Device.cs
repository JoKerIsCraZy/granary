using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Settings;

// Settings → Playback → This PC: choices about this PC's hardware. They are Granary's own, kept on the PC in
// IPlaybackDevicePreferences rather than in the profile, and count from the next title played.
public sealed partial class PlaybackSettingsViewModel
{
    /// <summary>Direct play without full hardware decoding (<see cref="IPlaybackDevicePreferences.DecodesInSoftware"/>).</summary>
    [ObservableProperty]
    public partial bool DecodesInSoftware { get; set; }

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

    partial void OnDecodesInSoftwareChanged(bool value)
    {
        if (value != _devicePreferences.DecodesInSoftware)
        {
            // Never fails: a save that does not work is logged, and the choice holds for this run.
            _ = _devicePreferences.SetDecodesInSoftwareAsync(value);
        }
    }
}
