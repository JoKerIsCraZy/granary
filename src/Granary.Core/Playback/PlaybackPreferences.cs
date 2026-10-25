using System.Text.Json;
using Granary.Core.Api.Settings;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>The profile's playback settings (docs/api-contract.md, 6.6), with the contract's defaults.</summary>
/// <param name="Quality">The start and replan <c>quality_preference</c>, lower-case: <c>auto</c> or a rung such as <c>1080p</c>.</param>
/// <param name="MaxBitrateKbps">The start's <c>bandwidth_cap_kbps</c>; null for no cap.</param>
/// <param name="SkipBackSeconds">The back skip of the buttons and the Left key.</param>
/// <param name="SkipForwardSeconds">The forward skip of the buttons and the Right key.</param>
public sealed record PlaybackPreferences(string Quality, int? MaxBitrateKbps, double SkipBackSeconds, double SkipForwardSeconds)
{
    public static PlaybackPreferences Default { get; } = new("auto", null, 10, 30);

    /// <summary>How text subtitles look (<c>playback.subtitle_appearance</c>).</summary>
    public SubtitleAppearance Subtitles { get; init; } = SubtitleAppearance.Default;

    /// <summary>Subtitles this much later, negative earlier (<c>player.subtitle_sync_ms</c>), within ±10 s.</summary>
    public int SubtitleDelayMs { get; init; }

    /// <summary>The delay is this device's own value, which outranks the profile's until it is cleared.</summary>
    public bool SubtitleDelayOnDevice { get; init; }

    /// <summary>What the player does with intros, recaps and credits, and when it offers the next episode.</summary>
    public MarkerSettings Markers { get; init; } = MarkerSettings.Default;

    /// <summary>This device's playback speed (<c>player.playback_speed</c>).</summary>
    public double Speed { get; init; } = 1;
}

/// <summary>Reads <see cref="PlaybackPreferences"/>; playback starts with the defaults when the server cannot say.</summary>
public sealed class PlaybackPreferencesReader(SettingsApi settings, ILogger<PlaybackPreferencesReader> logger)
{
    internal static readonly string[] Keys =
    [
        "playback.preferred_quality",
        "playback.max_bitrate_kbps",
        "player.video_skip_back_seconds",
        "player.video_skip_forward_seconds",
        "player.playback_speed",
    ];

    /// <summary>The setting's range (settings contract): the apps and the server keep to it.</summary>
    public const double MinSpeed = 0.25;

    public const double MaxSpeed = 3;

    // Read on their own: a server that does not know one of them refuses its whole batch, which must not cost the
    // quality and skip settings.
    internal static readonly string[] SubtitleKeys = ["playback.subtitle_appearance", "player.subtitle_sync_ms"];

    // The same for the marker settings (docs/api-contract.md, 11.2); they need the device header, which every call sends.
    internal static readonly string[] MarkerKeys =
    [
        "playback.intro_skip_mode",
        "playback.auto_skip_credits",
        "playback.auto_skip_recap",
        "playback.auto_play_next",
        "playback.next_up_prompt_seconds",
    ];

    // Asked on its own: a server from before it refuses a batch with it, and the marker settings must not go with it.
    internal static readonly string[] PreviewKeys = ["playback.auto_play_next_preview"];

    private const int MaxNextUpPromptSeconds = 120;

    /// <summary>The web player's limit for the subtitle delay.</summary>
    public const int MaxSubtitleDelayMs = 10_000;

    // The start accepts 100 to 1,000,000 kbps; a skip beyond ten minutes is no skip.
    private const int MinBitrateKbps = 100;
    private const int MaxBitrateKbps = 1_000_000;
    private const double MaxSkipSeconds = 600;

    public async Task<PlaybackPreferences> ReadAsync(Uri server, CancellationToken cancellationToken)
    {
        var subtitles = ReadBatchAsync(server, SubtitleKeys, "Subtitle", cancellationToken);
        var markers = ReadBatchAsync(server, MarkerKeys, "Marker", cancellationToken);
        var preview = ReadBatchAsync(server, PreviewKeys, "Next episode preview", cancellationToken);
        var preferences = await ReadMainAsync(server, cancellationToken).ConfigureAwait(false);
        if (await subtitles.ConfigureAwait(false) is { } subtitleValues)
        {
            int delay = Number(subtitleValues.Find(SubtitleKeys[1])) is { } ms ? (int)Math.Clamp(Math.Round(ms), -MaxSubtitleDelayMs, MaxSubtitleDelayMs) : 0;
            preferences = preferences with
            {
                Subtitles = SubtitleAppearance.From(subtitleValues.Find(SubtitleKeys[0])),
                SubtitleDelayMs = delay,
                SubtitleDelayOnDevice = subtitleValues.Items.FirstOrDefault(i => i.Key == SubtitleKeys[1])?.Source == "profile_device",
            };
        }
        if (await markers.ConfigureAwait(false) is { } markerValues)
        {
            preferences = preferences with { Markers = Markers(markerValues) };
        }
        if (await preview.ConfigureAwait(false) is { } previewValues && Bool(previewValues.Find(PreviewKeys[0])) is { } fromPreview)
        {
            preferences = preferences with { Markers = preferences.Markers with { AutoPlayNextPreview = fromPreview } };
        }
        return preferences;
    }

    private async Task<EffectiveSettings?> ReadBatchAsync(Uri server, string[] keys, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await settings.GetEffectiveAsync(server, keys, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            logger.LogInformation("{Batch} settings are not available ({Failure}); using the defaults", name, ApiFailureText.Describe(ex));
            return null;
        }
    }

    // A mode the client does not know neither prompts nor skips, as on Silo's web player; a missing one is the default.
    private static MarkerSettings Markers(EffectiveSettings values)
    {
        var defaults = MarkerSettings.Default;
        string mode = Text(values.Find(MarkerKeys[0]))?.ToLowerInvariant() switch
        {
            null => defaults.IntroSkipMode,
            MarkerSettings.Never => MarkerSettings.Never,
            MarkerSettings.Ask => MarkerSettings.Ask,
            MarkerSettings.Always => MarkerSettings.Always,
            _ => MarkerSettings.Never,
        };
        return new MarkerSettings(
            mode,
            Bool(values.Find(MarkerKeys[1])) ?? defaults.AutoSkipCredits,
            Bool(values.Find(MarkerKeys[2])) ?? defaults.AutoSkipRecap,
            Bool(values.Find(MarkerKeys[3])) ?? defaults.AutoPlayNext,
            Number(values.Find(MarkerKeys[4])) is { } seconds && seconds is >= 0 and <= MaxNextUpPromptSeconds
                ? (int)Math.Round(seconds)
                : defaults.NextUpPromptSeconds);
    }

    private async Task<PlaybackPreferences> ReadMainAsync(Uri server, CancellationToken cancellationToken)
    {
        EffectiveSettings values;
        try
        {
            values = await settings.GetEffectiveAsync(server, Keys, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            // An older server refuses a key it does not know (422): it has none of these settings then.
            logger.LogInformation("Playback settings are not available ({Failure}); using the defaults", ApiFailureText.Describe(ex));
            return PlaybackPreferences.Default;
        }
        var defaults = PlaybackPreferences.Default;
        return new PlaybackPreferences(
            Text(values.Find(Keys[0]))?.ToLowerInvariant() ?? defaults.Quality,
            Number(values.Find(Keys[1])) is { } bitrate && bitrate is >= MinBitrateKbps and <= MaxBitrateKbps ? (int)bitrate : defaults.MaxBitrateKbps,
            Number(values.Find(Keys[2])) is { } back && back is > 0 and <= MaxSkipSeconds ? back : defaults.SkipBackSeconds,
            Number(values.Find(Keys[3])) is { } forward && forward is > 0 and <= MaxSkipSeconds ? forward : defaults.SkipForwardSeconds)
        {
            Speed = Number(values.Find(Keys[4])) is { } speed && speed is >= MinSpeed and <= MaxSpeed ? speed : defaults.Speed,
        };
    }

    private static string? Text(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } text && text.GetString() is { Length: > 0 } s ? s.Trim() : null;

    private static bool? Bool(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static double? Number(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;
}
