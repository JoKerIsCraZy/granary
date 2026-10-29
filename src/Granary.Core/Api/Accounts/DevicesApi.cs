using Granary.Core.Api.Catalog;

namespace Granary.Core.Api.Accounts;

/// <summary>
/// Where the account is signed in, and the devices the profile watches on (docs/api-contract.md, 4.4). Signing a sign-in out
/// ends that client's access; forgetting a device only drops its settings, and its sign-in stays.
/// </summary>
public sealed class DevicesApi(SiloHttp http)
{
    /// <summary>How many sign-ins a page holds, as the web asks for.</summary>
    public const int SessionPageSize = 50;

    /// <summary>How many devices a page holds; the list is read whole.</summary>
    public const int DevicePageSize = 200;

    public Task<LoginSessionCapabilities> GetSessionCapabilitiesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/auth/sessions/capabilities", SiloAuth.Account, DevicesJsonContext.Default.LoginSessionCapabilities, cancellationToken);

    /// <summary>A page of the account's live sign-ins, newest first; <paramref name="cursor"/> is the last page's next cursor.</summary>
    public Task<LoginSessionCollection> ListSessionsAsync(Uri server, string? cursor = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/auth/sessions" + new QueryBuilder().Add("limit", SessionPageSize).Add("cursor", cursor), SiloAuth.Account,
            DevicesJsonContext.Default.LoginSessionCollection, cancellationToken);

    /// <summary>Signs a sign-in of the account out: that client loses access on its next request (204; 404 when it is gone).</summary>
    public Task SignOutSessionAsync(Uri server, string id, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Delete, server, "api/v2/auth/sessions/" + SiloHttp.PathSegment(id, nameof(id)), SiloAuth.Account, cancellationToken);

    /// <summary>The profile's devices, the one in use first, then by when they were last used; every page.</summary>
    public async Task<IReadOnlyList<UserDevice>> ListDevicesAsync(Uri server, CancellationToken cancellationToken = default)
    {
        var devices = new List<UserDevice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await http.GetAsync(server,
                "api/v2/devices" + new QueryBuilder().Add("scope", "profile").Add("limit", DevicePageSize).Add("cursor", cursor),
                SiloAuth.Profile, DevicesJsonContext.Default.UserDeviceCollection, cancellationToken).ConfigureAwait(false);
            devices.AddRange(page.Items);
            cursor = page.Page is { HasMore: true, NextCursor: { Length: > 0 } next } ? next : null;
            // A cursor seen before would read the same pages forever.
            if (cursor is not null && !seen.Add(cursor))
            {
                break;
            }
        }
        while (cursor is not null);
        return devices;
    }

    /// <summary>Gives a device a name of the profile's own, or with null shows the name it reports again; returns the device.</summary>
    public Task<UserDevice> RenameDeviceAsync(Uri server, string deviceId, string? name, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Patch, server, DevicePath(deviceId), SiloAuth.Profile, new DeviceRename(name),
            DevicesJsonContext.Default.DeviceRename, DevicesJsonContext.Default.UserDevice, cancellationToken);

    /// <summary>Removes the device's settings and drops it from the list until it is used again; its sign-in stays.</summary>
    public Task ForgetDeviceAsync(Uri server, string deviceId, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Delete, server, DevicePath(deviceId), SiloAuth.Profile, cancellationToken);

    /// <summary>Removes the settings changed on the device, so it follows the profile's again; it stays in the list.</summary>
    public Task ClearDeviceSettingsAsync(Uri server, string deviceId, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Delete, server, DevicePath(deviceId) + "/settings", SiloAuth.Profile, cancellationToken);

    private static string DevicePath(string deviceId) => "api/v2/devices/" + SiloHttp.PathSegment(deviceId, nameof(deviceId));
}
