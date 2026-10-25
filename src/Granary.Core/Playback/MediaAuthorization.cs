using Granary.Core.Auth;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

public enum MediaAuthorizationFailure
{
    /// <summary>The server has no login (any more): sign in again.</summary>
    SignedOut,

    /// <summary>No profile is selected; playback acts for one.</summary>
    NoProfile,

    /// <summary>A fresh token was needed and the server could not issue one right now.</summary>
    Unavailable,
}

public sealed class MediaAuthorizationException(MediaAuthorizationFailure failure, string message) : Exception(message)
{
    public MediaAuthorizationFailure Failure { get; } = failure;
}

/// <summary>The headers for the media requests of one load, and when their token expires.</summary>
public sealed record MediaCredentials(IReadOnlyList<KeyValuePair<string, string>> Headers, DateTimeOffset ExpiresAt)
{
    // The generated record ToString would print the token.
    public override string ToString() => $"MediaCredentials {{ ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>The headers for a playback's media requests; see <see cref="MediaAuthorization"/>.</summary>
public interface IMediaAuthorization
{
    /// <summary>The headers for a load from <paramref name="server"/>.</summary>
    /// <param name="rejected">The server refused the previous headers (HTTP 401): refresh whatever their age.</param>
    /// <exception cref="MediaAuthorizationException">There is no usable login or profile, or no fresh token when one is needed.</exception>
    Task<MediaCredentials> GetAsync(ServerEntry server, bool rejected, CancellationToken cancellationToken);
}

/// <summary>
/// The headers every media request of a playback carries (docs/api-contract.md, 9.3 and 9.4): the bearer
/// token and the acting profile, sent only to the server the media comes from. The player freezes them at
/// load time and keeps them for every range request, segment and reconnect, and the server accepts no
/// token past its expiry. So a load gets a token with at least half its lifetime ahead (refreshed first
/// otherwise), and after the server rejected the headers, a refreshed one in any case.
/// </summary>
public sealed class MediaAuthorization(
    ICredentialStore credentials,
    TokenRefresher refresher,
    TimeProvider time,
    ILogger<MediaAuthorization> logger) : IMediaAuthorization
{
    public async Task<MediaCredentials> GetAsync(ServerEntry server, bool rejected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var session = await credentials.GetAsync(server.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new MediaAuthorizationException(MediaAuthorizationFailure.SignedOut, "Sign in to play from this server.");
        if (rejected || NeedsFreshToken(session))
        {
            session = await RefreshAsync(server, session, rejected, cancellationToken).ConfigureAwait(false);
        }
        if (session.ProfileId is not { } profileId)
        {
            throw new MediaAuthorizationException(MediaAuthorizationFailure.NoProfile, "Choose a profile to play from this server.");
        }
        List<KeyValuePair<string, string>> headers =
        [
            new("Authorization", "Bearer " + session.AccessToken),
            new("X-Profile-Id", profileId),
        ];
        if (session.ProfileToken is { } profileToken)
        {
            headers.Add(new("X-Profile-Token", profileToken));
        }
        return new MediaCredentials(headers, session.AccessTokenExpiresAt);
    }

    private bool NeedsFreshToken(SessionCredentials session)
    {
        var remaining = session.AccessTokenExpiresAt - time.GetUtcNow();
        return remaining < TimeSpan.FromSeconds(Math.Max(0, session.LifetimeSeconds) / 2.0);
    }

    private async Task<SessionCredentials> RefreshAsync(ServerEntry server, SessionCredentials stale, bool rejected, CancellationToken cancellationToken)
    {
        var result = await refresher.RefreshAsync(server, stale, cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case RefreshStatus.Refreshed:
                return result.Credentials!;
            case RefreshStatus.Superseded:
                // Another login took the server's place meanwhile; play with that one.
                return await credentials.GetAsync(server.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new MediaAuthorizationException(MediaAuthorizationFailure.SignedOut, "Sign in to play from this server.");
            case RefreshStatus.SignedOut:
                throw new MediaAuthorizationException(MediaAuthorizationFailure.SignedOut, "The server ended the session. Sign in again to play.");
        }
        // The refresh failed for now. A token the server has not refused yet still plays until it expires.
        if (!rejected && stale.AccessTokenExpiresAt > time.GetUtcNow())
        {
            logger.LogInformation("Could not refresh the token before a load from server {ServerEntryId}; playing with the current one", server.Id);
            return stale;
        }
        throw new MediaAuthorizationException(MediaAuthorizationFailure.Unavailable, "The server could not renew the sign-in. Try again in a moment.");
    }
}
