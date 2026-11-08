using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

// AUDIO DELAY in the settings menu, as the Sync page of Silo's Android player has it (PlayerSettingsSheet): the sound 50 ms
// earlier or later a step, within 5 s either way, and Reset. A server title's delay is this device's (AudioDelay): the
// title follows a step at once and it is saved for this device, and a change made in Settings shows here; a server that
// does not know the setting gets no row. A local file starts with the delay as far as this run knows it (none on a cold
// --play, which reads nothing from the server), and a step there is only the player's.
public sealed partial class PlayerViewModel
{
    /// <summary>A step of Android's audio delay stepper.</summary>
    public const int AudioDelayStepMs = 50;

    private readonly AudioDelay? _audioDelay;

    /// <summary>The sound this many milliseconds after the picture, negative before it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioDelayText), nameof(CanDelayAudioEarlier), nameof(CanDelayAudioLater), nameof(CanResetAudioDelay))]
    public partial int AudioDelayMs { get; private set; }

    /// <summary>The row shows: a local file, or a server title whose server knows the setting.</summary>
    [ObservableProperty]
    public partial bool ShowsAudioDelay { get; private set; } = true;

    /// <summary>"0 ms", "+150 ms", "−200 ms", as Android's formatDelayMs writes it.</summary>
    public string AudioDelayText => DelayText(AudioDelayMs);

    public bool CanDelayAudioEarlier => AudioDelayMs > -AudioDelay.MaxMs;

    public bool CanDelayAudioLater => AudioDelayMs < AudioDelay.MaxMs;

    public bool CanResetAudioDelay => AudioDelayMs != 0;

    /// <summary>Moves the sound by <paramref name="deltaMs"/>; the menu steps by <see cref="AudioDelayStepMs"/>.</summary>
    public void ChangeAudioDelay(int deltaMs)
    {
        int delay = Math.Clamp(AudioDelayMs + deltaMs, -AudioDelay.MaxMs, AudioDelay.MaxMs);
        if (_playback is not null && _audioDelay is { } shared)
        {
            // The playback follows it into the player.
            shared.Set(delay);
            AudioDelayMs = shared.Milliseconds;
            return;
        }
        _engine.SetAudioDelay(delay / 1000.0);
        AudioDelayMs = delay;
    }

    public void ResetAudioDelay() => ChangeAudioDelay(-AudioDelayMs);

    // A title starts with this device's delay as far as this run knows it; a server title reads it again as it starts.
    private void ShowAudioDelay()
    {
        AudioDelayMs = _audioDelay?.Milliseconds ?? 0;
        ShowsAudioDelay = _playback is not { } playback || !playback.Preferences.AudioDelayRefused;
    }

    private void OnAudioDelayChanged(object? sender, EventArgs e)
    {
        if (_playback is not null && _audioDelay is { } shared)
        {
            AudioDelayMs = shared.Milliseconds;
        }
    }

    private static string DelayText(int milliseconds) => milliseconds == 0
        ? "0 ms"
        : (milliseconds > 0 ? "+" : "−") + Math.Abs(milliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms";
}
