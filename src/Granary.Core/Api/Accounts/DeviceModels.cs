using System.Text.Json.Serialization;
using Granary.Core.Api.Catalog;

namespace Granary.Core.Api.Accounts;

/// <summary>One live sign-in of the account, from <c>GET /api/v2/auth/sessions</c> (docs/api-contract.md, 4.4).</summary>
/// <param name="DeviceName">The name the client sent when it signed in (<c>X-Silo-Device-Name</c>), else its User-Agent; may be empty.</param>
/// <param name="DevicePlatform">The platform it sent (<c>X-Silo-Device-Platform</c>); null when it sent none.</param>
/// <param name="LastSeenAt">Its latest request, recorded at most once a minute; null until one is.</param>
/// <param name="Current">The sign-in this request came with: Granary's own.</param>
public sealed record LoginSession(string Id, string DeviceName, string IpAddress, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    string? DevicePlatform, DateTimeOffset? LastSeenAt, bool Current);

/// <summary>A page of the account's sign-ins, newest first, and Granary's own even when it falls on another page.</summary>
public sealed record LoginSessionCollection
{
    public IReadOnlyList<LoginSession> Items { get; init => field = value ?? []; } = [];

    public PageInfo? Page { get; init; }

    public LoginSession? CurrentSession { get; init; }
}

/// <summary><c>GET /api/v2/auth/sessions/capabilities</c>: whether the server lists and signs out sign-ins at all.</summary>
public sealed record LoginSessionCapabilities(bool Available);

/// <summary>A device the profile watched on, from <c>GET /api/v2/devices</c>: what its settings changes are kept under.</summary>
/// <param name="CustomName">The name the profile gave it, shown in place of <see cref="DeviceName"/>; null when none.</param>
/// <param name="IsCurrentDevice">The device this request came from (<c>X-Silo-Device-Id</c>): Granary on this PC.</param>
/// <param name="ChangedCount">How many settings are set differently on it.</param>
public sealed record UserDevice(string DeviceId, string DeviceName, string? CustomName, string DevicePlatform, DateTimeOffset? LastSeenAt,
    string ProfileId, string ProfileName, bool IsCurrentDevice, int ChangedCount);

public sealed record UserDeviceCollection
{
    public IReadOnlyList<UserDevice> Items { get; init => field = value ?? []; } = [];

    public PageInfo? Page { get; init; }
}

/// <summary>The body of <c>PATCH /api/v2/devices/{device_id}</c>: a name, or null to show the one the device reports again.</summary>
public sealed record DeviceRename([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomName);
