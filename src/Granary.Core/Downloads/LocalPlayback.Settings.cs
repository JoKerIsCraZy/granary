using System.Globalization;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Settings;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

// What the viewer sets while a download plays: the speed, the subtitle and audio delays, auto-play and the sleep timer's
// duration. Each holds in the player at once, as for a stream, and is saved to the server where a stream's is (the keys of
// ServerPlayback), when the title started with one signed in: a save that does not arrive, as without a connection, is
// logged and the change holds for this title. There is one file and no server stream, so no quality and no other version.
public sealed partial class LocalPlayback
{
    private const string AutoPlayNextKey = "playback.auto_play_next";
    private const string SpeedKey = "player.playback_speed";
    private const string SleepTimerKey = "player.sleep_timer_default_minutes";
    private const string SubtitleDelayKey = "player.subtitle_sync_ms";

    // As a stream's: a drag or a run of nudges is saved once it settles.
    private static readonly TimeSpan SpeedSettle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SubtitleDelaySettle = TimeSpan.FromMilliseconds(600);

    // A save that hangs must not hold the stop for the HTTP timeout.
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(10);

    private readonly AudioDelay? _audioDelay;
    private double _speed = 1;
    private int _subtitleDelayMs;
    private ITimer? _speedSave;
    private ITimer? _subtitleDelaySave;
    private Task _saves = Task.CompletedTask;

    public PlaybackPreferences Preferences { get; private set; } = PlaybackPreferences.Default;

    public double Speed => _speed;

    public int SubtitleDelayMs => _subtitleDelayMs;

    public IReadOnlyList<AvailableQuality> Qualities => [];

    public string QualityPreference => PlaybackPreferences.Default.Quality;

    public string? ActiveQuality => null;

    public bool IsBandwidthLimited => false;

    public IReadOnlyList<WatchFileVersion> Versions => [];

    /// <summary>None: adding subtitles and editing markers are for a file the server plays.</summary>
    public string? PlayingFileId => null;

    public string? RequestedFileId => null;

    public SubtitleSyncTracker? SubtitleSync => null;

    public string? SubtitleSyncKey => null;

    public void SelectQuality(string preference)
    {
        // The file on this PC is the quality it was downloaded in.
    }

    public void SelectVersion(string fileId)
    {
        // Another version streams: the title page's Version picker starts it.
    }

    public void ShowAddedSubtitle(string storedSubtitleId)
    {
        // A subtitle added on the server comes with the download's next refresh.
    }

    public void SetAutoPlayNext(bool enabled)
    {
        if (_stopping || Preferences.Markers.AutoPlayNext == enabled)
        {
            return;
        }
        Preferences = Preferences with { Markers = Preferences.Markers with { AutoPlayNext = enabled } };
        Save("auto-play", AutoPlayNextKey, enabled ? "true" : "false", SettingScopes.Profile);
    }

    public void SetSpeed(double speed)
    {
        speed = Math.Clamp(Math.Round(speed / 0.05) * 0.05, PlaybackPreferencesReader.MinSpeed, PlaybackPreferencesReader.MaxSpeed);
        if (_stopping || Math.Abs(speed - _speed) < 0.001)
        {
            return;
        }
        _speed = speed;
        _engine.SetSpeed(speed);
        _speedSave?.Dispose();
        _speedSave = _time.CreateTimer(_ => _dispatcher.Post(SaveSpeedNow), null, SpeedSettle, Timeout.InfiniteTimeSpan);
    }

    public void SetSleepTimerDefault(int minutes)
    {
        minutes = Math.Clamp(minutes, 0, PlaybackPreferencesReader.MaxSleepTimerMinutes);
        if (_stopping || Preferences.SleepTimerMinutes == minutes)
        {
            return;
        }
        Preferences = Preferences with { SleepTimerMinutes = minutes };
        Save("sleep timer", SleepTimerKey, minutes.ToString(CultureInfo.InvariantCulture), SettingScopes.ProfileDevice);
    }

    public void SetSubtitleDelay(int milliseconds)
    {
        int delay = Math.Clamp(milliseconds, -PlaybackPreferencesReader.MaxSubtitleDelayMs, PlaybackPreferencesReader.MaxSubtitleDelayMs);
        if (_stopping || delay == _subtitleDelayMs)
        {
            return;
        }
        _subtitleDelayMs = delay;
        // The file's own times are the title's: nothing but the viewer's delay moves them.
        _engine.SetSubtitleDelay(delay / 1000.0);
        _subtitleDelaySave?.Dispose();
        _subtitleDelaySave = _time.CreateTimer(_ => _dispatcher.Post(SaveSubtitleDelayNow), null, SubtitleDelaySettle, Timeout.InfiniteTimeSpan);
    }

    // A delay or a speed the viewer just set is saved at the stop, not when its wait would have ended.
    private void SaveSettingsNow()
    {
        SaveSubtitleDelayNow();
        SaveSpeedNow();
    }

    private void SaveSpeedNow()
    {
        if (_speedSave is null)
        {
            return;
        }
        _speedSave.Dispose();
        _speedSave = null;
        Save("speed", SpeedKey, _speed.ToString("0.##", CultureInfo.InvariantCulture), SettingScopes.ProfileDevice);
    }

    private void SaveSubtitleDelayNow()
    {
        if (_subtitleDelaySave is null)
        {
            return;
        }
        _subtitleDelaySave.Dispose();
        _subtitleDelaySave = null;
        // The profile's value, as the web writes it. One this device kept for itself would outrank it, so that one is cleared,
        // until a clear went through: one that failed is tried again with the next save.
        bool clearDevice = Preferences.SubtitleDelayOnDevice && !_deviceDelayCleared;
        Save("subtitle delay", SubtitleDelayKey, _subtitleDelayMs.ToString(CultureInfo.InvariantCulture), SettingScopes.Profile, clearDevice);
    }

    private bool _deviceDelayCleared;

    // In the order the viewer chose, whatever happens to playback meanwhile; nobody waits for one but the stop.
    private void Save(string kind, string key, string json, string scope, bool clearDevice = false)
    {
        if (_server is not { } server)
        {
            return;
        }
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement.Clone();
        var previous = _saves;
        _saves = SaveAsync();

        async Task SaveAsync()
        {
            await previous;
            using var timeout = new CancellationTokenSource(SaveTimeout, _time);
            try
            {
                // As the download's profile only: a profile switched to meanwhile gets none of this one's choices.
                string profile = _download.Record.Scope.ProfileId;
                await _settings.SetValueAsProfileAsync(server, key, value, scope, profile, timeout.Token);
                if (clearDevice)
                {
                    await _settings.ClearValueAsProfileAsync(server, key, SettingScopes.ProfileDevice, profile, timeout.Token);
                    _deviceDelayCleared = true;
                }
            }
            catch (SiloApiException ex) when (clearDevice && ex.Status == 404)
            {
                // Nothing to clear is what this asked for.
                _deviceDelayCleared = true;
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) || ex is ProfileChangedException)
            {
                _logger.LogInformation("The {Kind} setting was not saved ({Failure}); it holds for this title", kind, ApiFailureText.Describe(ex));
            }
            catch (Exception ex)
            {
                // A preference, not playback: whatever went wrong, the title plays on. Only its kind is logged.
                _logger.LogError("Saving the {Kind} setting failed unexpectedly ({Failure})", kind, DownloadLog.Failure(ex));
            }
        }
    }

    // This device's audio delay, as a stream takes it (ServerPlayback.StartAudioDelay): the value read for this title unless
    // the one held is newer; when it could not be read, as without a connection, the one this run knows if it is this
    // profile's. A change made while the title plays, here or in Settings, moves the sound at once.
    private void StartAudioDelay(PlaybackPreferences preferences, long read)
    {
        if (preferences.AudioDelayRefused)
        {
            _engine.SetAudioDelay(0);
            return;
        }
        if (_audioDelay is not { } delay)
        {
            _engine.SetAudioDelay((preferences.AudioDelayMs ?? 0) / 1000.0);
            return;
        }
        if (preferences.AudioDelayMs is { } fromServer)
        {
            delay.Take(fromServer, read);
        }
        else
        {
            delay.TakeUnread();
        }
        _engine.SetAudioDelay(delay.Milliseconds / 1000.0);
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
            _engine.SetAudioDelay(delay.Milliseconds / 1000.0);
        }
    }
}
