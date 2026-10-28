using System.Text.Json;
using Granary.Core.Api.Catalog;

namespace Granary.Core.Api.Settings;

/// <summary>One setting as it applies to the acting profile on this device, and where its value comes from.</summary>
/// <param name="Value">Any JSON value: a string, number, boolean, object, or null.</param>
public sealed record EffectiveSetting(string Key, JsonElement Value, string? Source);

public sealed record EffectiveSettings(IReadOnlyList<EffectiveSetting> Items, long Revision)
{
    public JsonElement? Find(string key) => Items.FirstOrDefault(i => i.Key == key)?.Value;
}

/// <summary>The body of <c>PUT /api/v2/settings/values/{key}</c>.</summary>
public sealed record SettingValueRequest(JsonElement Value);

/// <summary>Where a setting's explicit value is kept (docs/api-contract.md, 6.6).</summary>
public static class SettingScopes
{
    /// <summary>The acting profile, on every device.</summary>
    public const string Profile = "profile";

    /// <summary>
    /// The acting profile in the apps of this client family (<c>X-Silo-Client-Family</c>, <c>desktop</c> for Granary); it
    /// outranks the profile's value, and this device's outranks it.
    /// </summary>
    public const string ProfileClient = "profile_client";

    /// <summary>The acting profile on this device only (<c>X-Silo-Device-Id</c>); it outranks the profile's value.</summary>
    public const string ProfileDevice = "profile_device";
}

/// <summary><c>GET /api/v2/settings/overlay-config</c>: the server's card badge defaults, which a profile without a choice follows.</summary>
/// <param name="Defaults">The administrator's badge document, as a JSON string; null when none is set.</param>
public sealed record OverlayConfig(string? Defaults, bool Enabled);

/// <summary><c>GET /api/v2/settings/contract/capabilities</c>: which settings the server knows.</summary>
public sealed record SettingsContractCapabilities(int ApiVersion, int ManifestRevision, IReadOnlyList<string>? ClientFamilies);

/// <summary>The profile and device settings stored on the server (docs/api-contract.md, 6.6).</summary>
public sealed class SettingsApi(SiloHttp http)
{
    /// <summary>The settings contract version this client speaks; a server on another reports its revision only for its own.</summary>
    public const int ApiVersion = 1;

    /// <summary>The server's card badge defaults, which a profile without a choice follows.</summary>
    public Task<OverlayConfig> GetOverlayConfigAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/settings/overlay-config", SiloAuth.Profile, SiloJsonContext.Default.OverlayConfig, cancellationToken);

    public Task<SettingsContractCapabilities> GetCapabilitiesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/settings/contract/capabilities", SiloAuth.Profile, SiloJsonContext.Default.SettingsContractCapabilities,
            cancellationToken);

    /// <summary>
    /// The effective values of <paramref name="keys"/> for the acting profile and this device. A key the
    /// server's contract does not know fails the whole call with 422.
    /// </summary>
    public Task<EffectiveSettings> GetEffectiveAsync(Uri server, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        string query = new QueryBuilder().AddEach("keys", keys).ToString();
        return http.GetAsync(server, "api/v2/settings/values/effective" + query, SiloAuth.Profile, SiloJsonContext.Default.EffectiveSettings, cancellationToken);
    }

    /// <summary>Replaces the explicit value of <paramref name="key"/> at <paramref name="scope"/> (<see cref="SettingScopes"/>).</summary>
    public Task SetValueAsync(Uri server, string key, JsonElement value, string scope, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Put, server, ValuePath(key, scope), SiloAuth.Profile, new SettingValueRequest(value),
            SiloJsonContext.Default.SettingValueRequest, cancellationToken);

    /// <summary>Removes the explicit value of <paramref name="key"/> at <paramref name="scope"/>, so it inherits again; 404 when there is none.</summary>
    public Task ClearValueAsync(Uri server, string key, string scope, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Delete, server, ValuePath(key, scope), SiloAuth.Profile, cancellationToken);

    private static string ValuePath(string key, string scope) =>
        "api/v2/settings/values/" + SiloHttp.PathSegment(key, nameof(key)) + new QueryBuilder().Add("scope", scope);
}
