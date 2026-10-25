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
    ];

    // The start accepts 100 to 1,000,000 kbps; a skip beyond ten minutes is no skip.
    private const int MinBitrateKbps = 100;
    private const int MaxBitrateKbps = 1_000_000;
    private const double MaxSkipSeconds = 600;

    public async Task<PlaybackPreferences> ReadAsync(Uri server, CancellationToken cancellationToken)
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
            Number(values.Find(Keys[3])) is { } forward && forward is > 0 and <= MaxSkipSeconds ? forward : defaults.SkipForwardSeconds);
    }

    private static string? Text(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } text && text.GetString() is { Length: > 0 } s ? s.Trim() : null;

    private static double? Number(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;
}
