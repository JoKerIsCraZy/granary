using Granary.Core.Api.Auth;

namespace Granary.Core.Auth;

/// <summary>
/// One server's login session as stored on this machine (docs/api-contract.md, 5.3): the token pair,
/// when the access token expires, the acting profile, and the last known account.
/// <see cref="SessionId"/> is local: it names this login, so work started for it (a refresh, a retry)
/// never lands on a later login to the same server.
/// </summary>
public sealed record SessionCredentials(
    string SessionId,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    long LifetimeSeconds,
    string? ProfileId,
    string? ProfileToken,
    Account? Account)
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    // Anything longer than a year is not an access token lifetime the server would issue.
    private const long MaxLifetimeSeconds = 366L * 24 * 60 * 60;

    public static SessionCredentials Create(TokenPair tokens, DateTimeOffset now)
    {
        Validate(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresIn);
        return new(NewSessionId(), tokens.AccessToken, tokens.RefreshToken, now.AddSeconds(tokens.ExpiresIn), tokens.ExpiresIn,
            null, null, tokens.User);
    }

    public SessionCredentials WithRefreshedTokens(RefreshedTokens tokens, DateTimeOffset now)
    {
        Validate(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresIn);
        return this with
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            AccessTokenExpiresAt = now.AddSeconds(tokens.ExpiresIn),
            LifetimeSeconds = tokens.ExpiresIn,
        };
    }

    public static string NewSessionId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// True once the access token expires within 60 seconds, a margin capped at half its lifetime
    /// (Android's ProactiveRefreshPolicy).
    /// </summary>
    public bool ShouldRefresh(DateTimeOffset now)
    {
        var halfLifetime = TimeSpan.FromSeconds(Math.Max(0, LifetimeSeconds) / 2.0);
        var margin = halfLifetime < RefreshMargin ? halfLifetime : RefreshMargin;
        return now >= AccessTokenExpiresAt - margin;
    }

    /// <summary>Tokens and profile values travel in headers, so they must be visible ASCII.</summary>
    public static bool IsHeaderSafe(string value) => value.Length is > 0 and <= 8192 && value.All(c => c is > ' ' and <= '~');

    // The generated record ToString would print the tokens.
    public override string ToString() =>
        $"SessionCredentials {{ SessionId = {SessionId}, AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, ProfileId = {ProfileId ?? "none"} }}";

    private static void Validate(string accessToken, string refreshToken, long expiresIn)
    {
        if (!IsHeaderSafe(accessToken) || !IsHeaderSafe(refreshToken) || expiresIn is <= 0 or > MaxLifetimeSeconds)
        {
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server issued tokens this app cannot use.");
        }
    }
}
