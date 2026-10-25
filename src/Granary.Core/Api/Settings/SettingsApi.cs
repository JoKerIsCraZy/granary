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

/// <summary>The profile and device settings stored on the server (docs/api-contract.md, 6.6).</summary>
public sealed class SettingsApi(SiloHttp http)
{
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
}
