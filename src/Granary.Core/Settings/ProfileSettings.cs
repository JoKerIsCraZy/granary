using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Settings;

/// <summary>The keys of the settings pages (docs/api-contract.md, 6.6; the server's settings manifest).</summary>
public static class SettingKeys
{
    public const string PreferredQuality = "playback.preferred_quality";
    public const string MaxBitrateKbps = "playback.max_bitrate_kbps";
    public const string BufferingQualityMode = "playback.buffering_quality_mode";
    public const string IntroSkipMode = "playback.intro_skip_mode";
    public const string AutoSkipRecap = "playback.auto_skip_recap";
    public const string AutoSkipCredits = "playback.auto_skip_credits";
    public const string AutoPlayNextPreview = "playback.auto_play_next_preview";
    public const string AutoPlayNext = "playback.auto_play_next";
    public const string SkipBackSeconds = "player.video_skip_back_seconds";
    public const string SkipForwardSeconds = "player.video_skip_forward_seconds";
    public const string AudioLanguage = "playback.audio_language";
    public const string SubtitleLanguage = "playback.subtitle_language";
    public const string SubtitleMode = "playback.subtitle_mode";
    public const string ShowForcedSubtitles = "playback.show_forced_subtitles";
    public const string SubtitleAppearance = "playback.subtitle_appearance";
    public const string SubtitleSyncMs = "player.subtitle_sync_ms";

    // Keys a device can keep a value of its own for; the others are the profile's alone.
    private static readonly HashSet<string> DeviceOverridable =
    [
        PreferredQuality, MaxBitrateKbps, BufferingQualityMode, IntroSkipMode, AutoSkipRecap, AutoSkipCredits,
        AutoPlayNextPreview, AutoPlayNext, AudioLanguage, SubtitleLanguage, SubtitleMode, ShowForcedSubtitles,
        SubtitleAppearance, SubtitleSyncMs,
    ];

    public static bool IsDeviceOverridable(string key) => DeviceOverridable.Contains(key);
}

/// <summary>The acting profile's settings as the settings pages read and write them.</summary>
public interface IProfileSettings
{
    /// <summary>The effective values of <paramref name="keys"/>, with where each comes from; a key the server does not know is left out.</summary>
    Task<IReadOnlyDictionary<string, EffectiveSetting>> ReadAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken);

    /// <summary>
    /// Saves <paramref name="value"/> as the profile's. A value this device keeps for itself outranks it: with
    /// <paramref name="clearDevice"/> that one is cleared too, so the setting reads what was saved.
    /// </summary>
    Task SaveAsync(string key, JsonElement value, bool clearDevice, CancellationToken cancellationToken);

    /// <summary>Removes the profile's value, and this device's when the key has one, so the setting inherits again.</summary>
    Task ResetAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// As the web's settings screens write them (useProfileDefaultWriter): the profile's row first, so a failed clear
/// leaves the choice stored, then this device's row when it would keep shadowing it.
/// </summary>
public sealed class ProfileSettings(SettingsApi api, ISessionManager session, ILogger<ProfileSettings> logger) : IProfileSettings
{
    public async Task<IReadOnlyDictionary<string, EffectiveSetting>> ReadAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var server = Server;
        try
        {
            var values = await api.GetEffectiveAsync(server, keys, cancellationToken).ConfigureAwait(false);
            return values.Items.ToDictionary(i => i.Key);
        }
        catch (SiloApiException ex) when (ex.Status == 422)
        {
            // An older server refuses the whole batch over one key it does not know: each on its own, then.
            logger.LogInformation("The server does not know every setting of the page; reading them one by one");
        }
        var reads = keys.Select(async key =>
        {
            try
            {
                return (await api.GetEffectiveAsync(server, [key], cancellationToken).ConfigureAwait(false)).Items;
            }
            catch (SiloApiException ex) when (ex.Status == 422)
            {
                return [];
            }
        });
        return (await Task.WhenAll(reads).ConfigureAwait(false)).SelectMany(items => items).ToDictionary(i => i.Key);
    }

    public async Task SaveAsync(string key, JsonElement value, bool clearDevice, CancellationToken cancellationToken)
    {
        var server = Server;
        await api.SetValueAsync(server, key, value, SettingScopes.Profile, cancellationToken).ConfigureAwait(false);
        if (clearDevice && SettingKeys.IsDeviceOverridable(key))
        {
            await ClearAsync(server, key, SettingScopes.ProfileDevice, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResetAsync(string key, CancellationToken cancellationToken)
    {
        var server = Server;
        await ClearAsync(server, key, SettingScopes.Profile, cancellationToken).ConfigureAwait(false);
        if (SettingKeys.IsDeviceOverridable(key))
        {
            await ClearAsync(server, key, SettingScopes.ProfileDevice, cancellationToken).ConfigureAwait(false);
        }
    }

    private Uri Server => session.ActiveServer?.BaseUri ?? throw new InvalidOperationException("No server is signed in.");

    private async Task ClearAsync(Uri server, string key, string scope, CancellationToken cancellationToken)
    {
        try
        {
            await api.ClearValueAsync(server, key, scope, cancellationToken).ConfigureAwait(false);
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // Nothing to clear is what this asked for.
        }
    }
}
