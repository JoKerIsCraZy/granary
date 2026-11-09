using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

// The volume and mute: the slider, M, Up and Down when they set the volume, and the minimized bar's Mute and volume keys.
// As the web player keeps them for its browser (player-volume and player-muted in localStorage), this PC keeps the viewer's
// (IPlaybackDevicePreferences.Volume), and every title starts with them, a file played with --play too. Only the viewer's
// own changes are kept: a volume the engine reports by itself, a new mpv's own or one an administrator set (set_volume),
// plays but is not kept. A Watch Party sets neither.
public sealed partial class PlayerViewModel
{
    // A drag along the slider or a held volume key passes many values: the one they settle on is saved.
    private static readonly TimeSpan VolumeSettles = TimeSpan.FromMilliseconds(500);

    // The viewer's volume and mute: this PC's at first, then the viewer's latest change; null before either, while the
    // engine's own hold. On the UI thread, as everything here.
    private PlayerVolume? _chosenVolume;
    private bool _volumeUnsaved;
    private ITimer? _volumeSettle;
    // The viewer's changes so far: a timer that was due as a later change came is not that change's.
    private long _volumeChanges;
    // Set while the volume shown follows the engine's (OnVolumeChanged).
    private bool _showingEngineState;

    /// <summary>0 to 100; muted, it keeps the level that Unmute goes back to.</summary>
    [ObservableProperty]
    public partial double Volume { get; set; } = 100;

    [ObservableProperty]
    public partial bool IsMuted { get; private set; }

    /// <summary>Raises or lowers the volume by <paramref name="delta"/> percentage points (Up and Down keys).</summary>
    public void ChangeVolume(double delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    // Kept with the viewer's own level, also after a volume set from elsewhere: that one was the title's alone.
    [RelayCommand]
    private void ToggleMute()
    {
        bool muted = !IsMuted;
        _engine.SetMuted(muted);
        Choose(new PlayerVolume(_chosenVolume?.Percent ?? Volume, muted));
    }

    // A value shown from the engine is not the viewer's change, even when the engine has moved on since: a new mpv says its
    // own volume before it gets the viewer's. Any other is (the slider, the volume keys), and goes to the engine even when
    // the engine seems to have it: mpv says a volume a moment after it was set, so what it said last may be older than
    // the viewer's last change, as when a drag goes away and back before mpv answers. It also ends a mute, as on the web
    // player, likewise even when none seems on: a Mute just pressed may not be reported yet.
    partial void OnVolumeChanged(double value)
    {
        if (_showingEngineState)
        {
            return;
        }
        _engine.SetMuted(false);
        _engine.SetVolume(value);
        Choose(new PlayerVolume(value, IsMuted: false));
    }

    // What this PC kept shows at once, before the engine says anything.
    private void TakeKeptVolume()
    {
        _chosenVolume = _devicePreferences?.Volume;
        if (_chosenVolume is { } kept)
        {
            ShowVolume(kept.Percent, kept.IsMuted);
        }
    }

    // mpv keeps its volume from one file to the next, but not past a start of Granary, and one set from elsewhere was only
    // for the title it played.
    private void StartWithChosenVolume()
    {
        if (_chosenVolume is not { } chosen)
        {
            return;
        }
        _engine.SetVolume(chosen.Percent);
        _engine.SetMuted(chosen.IsMuted);
        ShowVolume(chosen.Percent, chosen.IsMuted);
    }

    private void ShowVolume(double volume, bool muted)
    {
        IsMuted = muted;
        _showingEngineState = true;
        Volume = volume;
        _showingEngineState = false;
    }

    // Without this PC's choices, as in tests that leave them out, the choice still holds for the player's next title.
    private void Choose(PlayerVolume volume)
    {
        _chosenVolume = volume;
        if (_devicePreferences is null)
        {
            return;
        }
        _volumeUnsaved = true;
        long change = ++_volumeChanges;
        _volumeSettle?.Dispose();
        _volumeSettle = _time.CreateTimer(_ => _dispatcher.Post(() =>
        {
            if (change == _volumeChanges)
            {
                SaveVolumeNow();
            }
        }), null, VolumeSettles, Timeout.InfiniteTimeSpan);
    }

    // A change still settling is saved at once: the player is left or closed, or the app closes.
    private void SaveVolumeNow()
    {
        _volumeSettle?.Dispose();
        _volumeSettle = null;
        if (!_volumeUnsaved || _chosenVolume is not { } chosen)
        {
            return;
        }
        _volumeUnsaved = false;
        // Never fails: a save that does not work is logged, and the choice holds for this run.
        _ = _devicePreferences?.SetVolumeAsync(chosen);
    }
}
