using System.Globalization;
using System.Text.Json;
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

    private double _speed = 1;

    public IReadOnlyList<AvailableQuality> Qualities =>
        _plan is { } plan ? [.. plan.AvailableQualities.Where(q => q.Label.Length > 0 && q.Label != SourceQuality)] : [];

    public string QualityPreference => _context?.Preferences.Quality ?? PlaybackPreferences.Default.Quality;

    public string? ActiveQuality => _plan?.ActiveQuality;

    public bool IsBandwidthLimited => _plan?.BandwidthLimit is not null;

    public IReadOnlyList<WatchFileVersion> Versions => _context is { } context ? [.. context.Watch.Versions.Where(v => v.FileId.Length > 0)] : [];

    public string? PlayingFileId => PlayingVersion?.FileId;

    public string? RequestedFileId => _plan?.RequestedMediaFileId is { Length: > 0 } requested ? requested : _context?.Version.FileId;

    public double Speed => _speed;

    public void SelectQuality(string preference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preference);
        if (!CanChangeTracks || _context is not { } context || string.Equals(context.Preferences.Quality, preference, StringComparison.Ordinal))
        {
            return;
        }
        // Every later replan and attempt asks with it too (8.11).
        _context = context with { Preferences = context.Preferences with { Quality = preference } };
        TracksChanged?.Invoke(this, EventArgs.Empty);
        Recover(ChangeQualityAsync);
    }

    public void SelectVersion(string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        if (!CanChangeTracks || _context is not { } context || PlayingVersion?.FileId == fileId
            || context.Watch.Versions.FirstOrDefault(v => v.FileId == fileId) is not { } version)
        {
            return;
        }
        double position = SourcePosition;
        _logger.LogInformation("Switching to another file of the title at {Position:F1} s", position);
        // Another file has its own subtitle order: its automatic choice applies.
        _context = context with { Version = version, SubtitlePick = null };
        Recover(() => RestartAsync(position));
    }

    public void SetSpeed(double speed)
    {
        speed = Math.Clamp(Math.Round(speed / 0.05) * 0.05, PlaybackPreferencesReader.MinSpeed, PlaybackPreferencesReader.MaxSpeed);
        if (Math.Abs(speed - _speed) < 0.001)
        {
            return;
        }
        _speed = speed;
        _engine.SetSpeed(speed);
        if (_context is { } context)
        {
            // This device's speed, as the apps keep it.
            var value = JsonDocument.Parse(speed.ToString("0.##", CultureInfo.InvariantCulture)).RootElement.Clone();
            SaveChoice("speed", cancellationToken =>
                _settings.SetValueAsync(context.Server.BaseUri, SpeedKey, value, SettingScopes.ProfileDevice, cancellationToken));
        }
    }

    // The quality the viewer picked, from where the viewer is; a refusal leaves the stream that plays.
    private async Task ChangeQualityAsync()
    {
        ResetPlanChain();
        double position = SourcePosition;
        _logger.LogInformation("Asking for a stream of quality {Quality} at {Position:F1} s", QualityPreference, position);
        var decision = await ReplanAsync(ReplanOperations.QualityChange, position, [],
            refused: refusal => _logger.LogWarning("The server did not change the quality ({Failure}); playing on", ApiFailureText.Describe(refusal)));
        if (decision is null)
        {
            return;
        }
        if (!decision.IsPlayable)
        {
            _logger.LogWarning("The server has no stream of that quality ({Reason}); playing on", decision.Terminal?.Reason);
            return;
        }
        await AdoptAsync(decision, newSession: false, restorePosition: position);
    }
}
