using System.Globalization;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Settings;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// The settings menu (docs/api-contract.md, 8.11): the plan's qualities, the title's files, and the speed.
public sealed partial class ServerPlayback
{
    private const string SpeedKey = "player.playback_speed";

    // An older server still lists the source as a rung; "Auto" is the way back to it.
    private const string SourceQuality = "original";

    // A drag along the speed track passes several stops: the speed is saved once it settles.
    private static readonly TimeSpan SpeedSettle = TimeSpan.FromSeconds(1);

    private double _speed = 1;
    private ITimer? _speedSave;

    // The viewer's latest quality or file until a stream plays it: one picked while the stream is being replaced
    // goes out once that is done, as an audio pick does.
    private string? _pendingQuality;
    private string? _pendingVersion;

    public IReadOnlyList<AvailableQuality> Qualities =>
        _plan is { } plan ? [.. plan.AvailableQualities.Where(q => q.Label.Length > 0 && q.Label != SourceQuality)] : [];

    public string QualityPreference => _pendingQuality ?? _context?.Preferences.Quality ?? PlaybackPreferences.Default.Quality;

    public string? ActiveQuality => _plan?.ActiveQuality;

    public bool IsBandwidthLimited => _plan?.BandwidthLimit is not null;

    public IReadOnlyList<WatchFileVersion> Versions => _context is { } context ? [.. context.Watch.Versions.Where(v => v.FileId.Length > 0)] : [];

    public string? PlayingFileId => PlayingVersion?.FileId;

    public string? RequestedFileId => _plan?.RequestedMediaFileId is { Length: > 0 } requested ? requested : _context?.Version.FileId;

    public double Speed => _speed;

    public void SelectQuality(string preference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preference);
        if (!CanChangeTracks || preference == QualityPreference)
        {
            return;
        }
        _pendingQuality = preference;
        TracksChanged?.Invoke(this, EventArgs.Empty);
        ApplyPendingQuality();
    }

    public void SelectVersion(string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        if (!CanChangeTracks || _context is not { } context || context.Watch.Versions.All(v => v.FileId != fileId)
            || fileId == _pendingVersion || (_pendingVersion is null && fileId == context.Version.FileId && fileId == PlayingVersion?.FileId))
        {
            return;
        }
        _pendingVersion = fileId;
        ApplyPendingVersion();
    }

    public void SetSpeed(double speed)
    {
        speed = Math.Clamp(Math.Round(speed / 0.05) * 0.05, PlaybackPreferencesReader.MinSpeed, PlaybackPreferencesReader.MaxSpeed);
        if (_context is null || _stopping || Math.Abs(speed - _speed) < 0.001)
        {
            return;
        }
        _speed = speed;
        _engine.SetSpeed(speed);
        _speedSave?.Dispose();
        _speedSave = _time.CreateTimer(_ => _dispatcher.Post(SaveSpeedNow), null, SpeedSettle, Timeout.InfiniteTimeSpan);
    }

    // This device's speed, as the apps keep it.
    private void SaveSpeedNow()
    {
        if (_speedSave is null)
        {
            return;
        }
        _speedSave.Dispose();
        _speedSave = null;
        var context = _context!;
        using var document = JsonDocument.Parse(_speed.ToString("0.##", CultureInfo.InvariantCulture));
        var value = document.RootElement.Clone();
        SaveChoice("speed", cancellationToken =>
            _settings.SetValueAsync(context.Server.BaseUri, SpeedKey, value, SettingScopes.ProfileDevice, cancellationToken));
    }

    private void ApplyPendingQuality()
    {
        if (_pendingQuality is not { } preference || _recovering || !_started)
        {
            return;
        }
        _pendingQuality = null;
        var context = _context!;
        string previous = context.Preferences.Quality;
        if (preference == previous)
        {
            return;
        }
        // Every later replan and attempt asks with it too (8.11).
        _context = context with { Preferences = context.Preferences with { Quality = preference } };
        Recover(() => ChangeQualityAsync(preference, previous));
    }

    // The quality the viewer picked, from where the viewer is. A refusal leaves the stream that plays, and the
    // quality it plays stays the one asked for, so the pick can be made again.
    private async Task ChangeQualityAsync(string preference, string previous)
    {
        ResetPlanChain();
        double position = SourcePosition;
        _logger.LogInformation("Asking for a stream of quality {Quality} at {Position:F1} s", preference, position);
        var decision = await ReplanAsync(ReplanOperations.QualityChange, position, [], refused: refusal =>
        {
            _logger.LogWarning("The server did not change the quality ({Failure}); playing on", ApiFailureText.Describe(refusal));
            KeepQuality(preference, previous);
        });
        if (decision is null)
        {
            return;
        }
        if (!decision.IsPlayable)
        {
            _logger.LogWarning("The server has no stream of that quality ({Reason}); playing on", decision.Terminal?.Reason);
            KeepQuality(preference, previous);
            return;
        }
        await AdoptAsync(decision, newSession: false, restorePosition: position);
    }

    private void KeepQuality(string refused, string previous)
    {
        if (_context is { } context && context.Preferences.Quality == refused)
        {
            _context = context with { Preferences = context.Preferences with { Quality = previous } };
            TracksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyPendingVersion()
    {
        if (_pendingVersion is not { } fileId || _recovering || !_started)
        {
            return;
        }
        _pendingVersion = null;
        var context = _context!;
        if ((fileId == context.Version.FileId && fileId == PlayingVersion?.FileId)
            || context.Watch.Versions.FirstOrDefault(v => v.FileId == fileId) is not { } version)
        {
            return;
        }
        double position = SourcePosition;
        _logger.LogInformation("Switching to another file of the title at {Position:F1} s", position);
        // Another file has its own subtitle order: its automatic choice applies.
        _context = context with { Version = version, SubtitlePick = null };
        Recover(() =>
        {
            // A stop meanwhile waits for the start, as for any other, and ends the session it makes.
            _starting = Guarded(() => ChangeVersionAsync(context, position));
            return _starting;
        });
    }

    // A new attempt with the other file; until the server gives one, the file that plays goes on, and stays the one
    // a later attempt asks for if the server gives none.
    private async Task ChangeVersionAsync(PlaybackContext previous, double position)
    {
        int generation = _generation;
        string attemptId = Guid.NewGuid().ToString();
        PlaybackDecision decision;
        for (int tries = 0; ; tries++)
        {
            try
            {
                decision = await _starter.StartAsync(_context!, attemptId, position, CancellationToken.None);
                break;
            }
            catch (SiloApiException ex) when (ex is { Status: 409, Code: ProblemCodes.InstallationChanged } && tries == 0)
            {
                // The server was reinstalled: as for any start, its new installation once, then the file again.
                if (!await RefreshCapabilitiesAsync())
                {
                    return;
                }
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
            {
                _logger.LogWarning("The server did not start the other file ({Failure}); playing on", ApiFailureText.Describe(ex));
                KeepVersion(previous, generation);
                return;
            }
        }
        if (Superseded(generation) || !decision.IsPlayable)
        {
            if (decision.SessionId is { } session)
            {
                // A session made for a start that is not taken: it ends now, not when it idles out.
                Retire(NewSession(session), position: 0);
            }
            if (!Superseded(generation))
            {
                _logger.LogWarning("The server has no stream of the other file ({Reason}); playing on", decision.Terminal?.Reason);
                KeepVersion(previous, generation);
            }
            return;
        }
        StopTimers();
        _attemptId = attemptId;
        ResetPlanChain();
        await AdoptAsync(decision, newSession: true, restorePosition: position);
    }

    private void KeepVersion(PlaybackContext previous, int generation)
    {
        if (!Superseded(generation) && _context is { } context)
        {
            _context = context with { Version = previous.Version, SubtitlePick = previous.SubtitlePick };
            TracksChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
