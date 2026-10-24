namespace Granary.Core.Api;

/// <summary>Problem codes the client branches on (silo-server/internal/apiv2/problem.go).</summary>
public static class ProblemCodes
{
    public const string Unknown = "unknown";
    public const string AuthenticationRequired = "authentication_required";
    public const string InvalidToken = "invalid_token";
    public const string SessionExpired = "session_expired";
    public const string TokenRefreshRequired = "token_refresh_required";
    public const string PermissionDenied = "permission_denied";
    public const string ProfileVerificationRequired = "profile_verification_required";
    public const string PasswordChangeRequired = "password_change_required";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string ValidationFailed = "validation_failed";
    public const string InvalidCursor = "invalid_cursor";
    public const string RateLimited = "rate_limited";
    public const string DependencyUnavailable = "dependency_unavailable";
    public const string InstallationChanged = "installation_changed";
    public const string PlaybackSessionEnded = "playback_session_ended";
    public const string DeviceLoginExpired = "device_login_expired";
    public const string ClientUpgradeRequired = "client_upgrade_required";
}
