namespace Granary.Core.Api.Auth;

/// <summary><c>POST /api/v2/auth/login</c> body. <see cref="Provider"/> stays null (omitted) for password sign-in.</summary>
public sealed record LoginRequest(string Username, string Password, string? Provider = null);

/// <summary>Credentials of a new login session (<c>TokenPair</c>).</summary>
public sealed record TokenPair(string AccessToken, string RefreshToken, long ExpiresIn, Account User);

/// <summary>The signed-in login account (<c>Account</c>), not a household profile.</summary>
public sealed record Account(
    string Id,
    string Username,
    string Email,
    string Role,
    IReadOnlyList<string> Permissions,
    bool DownloadAllowed,
    bool PasswordChangeRequired,
    Impersonation? Impersonation)
{
    public bool IsAdmin => Role == "admin";
}

public sealed record Impersonation(bool Active, string ImpersonatorUserId, string ImpersonatorUsername);

/// <summary><c>POST /api/v2/auth/refresh</c> body.</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary><c>POST /api/v2/auth/refresh</c> answer. Old tokens stay valid until their own expiry.</summary>
public sealed record RefreshedTokens(string AccessToken, string RefreshToken, long ExpiresIn);

/// <summary><c>GET /api/v2/auth/providers</c>.</summary>
public sealed record AuthProviderCollection(IReadOnlyList<AuthProvider> Items, bool PasswordLogin);

public sealed record AuthProvider(string Id, string DisplayName, string Mode, bool Default, string? IconUrl);
