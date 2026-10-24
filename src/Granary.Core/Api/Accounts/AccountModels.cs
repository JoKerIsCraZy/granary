namespace Granary.Core.Api.Accounts;

/// <summary><c>POST /api/v2/account/password</c> body.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary><c>GET /api/v2/account/password/capability</c>.</summary>
public sealed record AccountPasswordCapability(
    string State,
    bool Allowed,
    long MinimumPasswordLength,
    long MaximumPasswordBytes,
    bool RequiresCurrentPassword);
