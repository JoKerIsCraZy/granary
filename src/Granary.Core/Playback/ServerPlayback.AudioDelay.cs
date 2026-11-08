namespace Granary.Core.Playback;

// This device's audio delay (AudioDelay): the player starts with it and follows a change made while the title plays, as
// Android's SiloPlaybackService passes the setting on to its DelayAudioProcessor. mpv moves only the sound: subtitles keep
// to the picture, so the subtitle delay and subtitle sync, which time subtitles against the picture, are not affected.
public sealed partial class ServerPlayback
{
    private readonly AudioDelay? _audioDelay;

    // The device's value as read for this title: taken unless a change made here is still on its way to the server.
    private void StartAudioDelay(int fromSettings)
    {
        if (_audioDelay is not { } delay)
        {
            _engine.SetAudioDelay(fromSettings / 1000.0);
            return;
        }
        delay.Take(fromSettings);
        ApplyAudioDelay(delay);
        delay.Changed -= OnAudioDelayChanged;
        delay.Changed += OnAudioDelayChanged;
    }

    // After the stop the engine may play the next title, which follows the delay itself.
    private void StopAudioDelay()
    {
        if (_audioDelay is { } delay)
        {
            delay.Changed -= OnAudioDelayChanged;
        }
    }

    private void OnAudioDelayChanged(object? sender, EventArgs e)
    {
        if (!_stopping && _audioDelay is { } delay)
        {
            ApplyAudioDelay(delay);
        }
    }

    // The engine leaves mpv.conf's own audio-delay alone until it is given another value than 0.
    private void ApplyAudioDelay(AudioDelay delay) => _engine.SetAudioDelay(delay.Milliseconds / 1000.0);
}
