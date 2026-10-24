namespace Granary.Core.Api.Auth;

/// <summary><c>GET /api/v2/auth/device/capability</c>. Offer device sign-in only when <see cref="State"/> is <c>available</c>.</summary>
public sealed record DeviceLoginCapability(string State, bool Cancel, bool OpenedSignal, IReadOnlyList<int> ProtocolVersions, string Revision)
{
    public bool IsAvailable => State == "available";
}

/// <summary><c>POST /api/v2/auth/device/start</c> body.</summary>
public sealed record StartDeviceLoginRequest(string? DeviceName, string? DevicePlatform);

/// <summary>
/// <c>POST /api/v2/auth/device/start</c> answer (201). <see cref="DeviceCode"/> is a secret; show
/// <see cref="UserCode"/> (grouped 4+4) and encode <see cref="VerificationUriComplete"/> as the QR code.
/// </summary>
public sealed record DeviceLoginStart(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    DateTimeOffset ExpiresAt,
    long ExpiresIn,
    long Interval,
    string DeviceName,
    string DevicePlatform);

/// <summary><c>POST /api/v2/auth/device/poll</c> and <c>/cancel</c> body.</summary>
public sealed record DeviceCodeRequest(string DeviceCode);

/// <summary><c>POST /api/v2/auth/device/poll</c> answer; <see cref="Tokens"/> only when approved, exactly once.</summary>
public sealed record DeviceLoginPoll(
    string Status,
    long PollAfter,
    bool Opened,
    DateTimeOffset? ExpiresAt,
    TokenPair? Tokens,
    string ProfileId,
    string ProfileToken,
    bool Temporary);

/// <summary><c>POST /api/v2/auth/device/cancel</c> answer.</summary>
public sealed record DeviceLoginCancel(string Status);

public static class DeviceLoginStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Consumed = "consumed";
    public const string Canceled = "canceled";
    public const string Expired = "expired";
}
