using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Core.ViewModels;

// The settings menu of Silo's web player (QualityMenu): SPEED, VERSION when the title has several files, QUALITY when
// the plan offers rungs; the gear names the quality, and a speed other than 1× shows beside it.
public sealed partial class PlayerViewModel
{
    [ObservableProperty]
    public partial IReadOnlyList<QualityOption> QualityOptions { get; private set; } = [];

    [ObservableProperty]
    public partial bool HasQualityOptions { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<VersionOption> VersionOptions { get; private set; } = [];

    [ObservableProperty]
    public partial bool HasVersionOptions { get; private set; }

    [ObservableProperty]
    public partial double Speed { get; private set; } = 1;

    /// <summary>"1.5×".</summary>
    [ObservableProperty]
    public partial string SpeedText { get; private set; } = PlaybackSpeeds.Format(1);

    /// <summary>The speed beside the gear while it is not 1×; empty at 1×.</summary>
    [ObservableProperty]
    public partial string SpeedBadge { get; private set; } = "";

    /// <summary>The quality on the gear ("Auto", "1080p High"); empty without a quality menu.</summary>
    [ObservableProperty]
    public partial string SettingsLabel { get; private set; } = "";

    /// <summary>The viewer's pick in QUALITY: a new plan from where the viewer is; not saved, as on the web.</summary>
    public void SelectQuality(string preference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preference);
        _playback?.SelectQuality(preference);
        ShowSettings();
    }

    /// <summary>The viewer's pick in VERSION: that file from where the viewer is.</summary>
    public void SelectVersion(string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        _playback?.SelectVersion(fileId);
    }

    /// <summary>The viewer's speed: at once, and kept for this device.</summary>
    public void SetSpeed(double speed)
    {
        // A Watch Party plays at the room's speed, with the room's corrections.
        if (IsWatchParty)
        {
            return;
        }
        if (_playback is { } playback)
        {
            playback.SetSpeed(speed);
        }
        else
        {
            _engine.SetSpeed(speed);
            Speed = speed;
        }
        ShowSettings();
    }

    private void ShowSettings()
    {
        if (_playback is { } playback)
        {
            QualityOptions = QualityOption.For(playback.Qualities, playback.QualityPreference, playback.ActiveQuality, playback.IsBandwidthLimited);
            // A Watch Party plays one file for everyone, as the web player keeps it.
            VersionOptions = IsWatchParty ? [] : VersionOption.For(playback.Versions, playback.PlayingFileId, playback.RequestedFileId);
            Speed = playback.Speed;
        }
        else
        {
            QualityOptions = [];
            VersionOptions = [];
        }
        HasQualityOptions = QualityOptions.Count > 0;
        HasVersionOptions = VersionOptions.Count > 0;
        SpeedText = PlaybackSpeeds.Format(Speed);
        SpeedBadge = Math.Abs(Speed - 1) < 0.001 ? "" : SpeedText;
        SettingsLabel = HasQualityOptions ? QualityOptions.FirstOrDefault(o => o.IsSelected)?.Label ?? "Quality" : "";
    }
}
