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
    public const string LocalLoginDisabled = "local_login_disabled";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string ValidationFailed = "validation_failed";
    public const string MalformedRequest = "malformed_request";
    public const string InvalidCursor = "invalid_cursor";
    public const string RateLimited = "rate_limited";
    public const string DependencyUnavailable = "dependency_unavailable";
    public const string CapabilityNotConfigured = "capability_not_configured";

    /// <summary>409 on a playback mutation: the <c>installation_id</c> is not this server's. Refetch capabilities, start a new attempt.</summary>
    public const string InstallationChanged = "installation_changed";

    /// <summary>410 on progress, replan or media: an administrator ended the session. Leave playback; do not restart.</summary>
    public const string PlaybackSessionEnded = "playback_session_ended";

    /// <summary>409 on progress: the same sequence was sent with a different sample (a client bug).</summary>
    public const string ProgressConflict = "progress_conflict";

    /// <summary>409 on start: the <c>playback_attempt_id</c> was used before with a different body.</summary>
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string DeviceLoginExpired = "device_login_expired";
    public const string ClientUpgradeRequired = "client_upgrade_required";
}
