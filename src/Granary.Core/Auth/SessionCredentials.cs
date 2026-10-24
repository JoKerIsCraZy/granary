using Granary.Core.Api.Auth;

namespace Granary.Core.Auth;

/// <summary>
/// One server's login session as stored on this machine (docs/api-contract.md, 5.3): the token pair,
/// when the access token expires, the acting profile, and the last known account.
/// </summary>
public sealed record SessionCredentials(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    long LifetimeSeconds,
    string? ProfileId,
    string? ProfileToken,
    Account? Account)
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    public static SessionCredentials Create(TokenPair tokens, DateTimeOffset now) =>
        new(tokens.AccessToken, tokens.RefreshToken, now.AddSeconds(tokens.ExpiresIn), tokens.ExpiresIn, null, null, tokens.User);

    public SessionCredentials WithRefreshedTokens(RefreshedTokens tokens, DateTimeOffset now) =>
        this with
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            AccessTokenExpiresAt = now.AddSeconds(tokens.ExpiresIn),
            LifetimeSeconds = tokens.ExpiresIn,
        };

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

    // The generated record ToString would print the tokens.
    public override string ToString() =>
        $"SessionCredentials {{ AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, ProfileId = {ProfileId ?? "none"} }}";
}
