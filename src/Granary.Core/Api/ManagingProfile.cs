using Granary.Core.Auth;
using Granary.Core.Servers;

namespace Granary.Core.Api;

/// <summary>
/// The profile a household write goes out as: the server lets only the household's verified primary profile create, change
/// and delete profiles (docs/api-contract.md, 6.7). Either the profile that acts now (<see cref="Acting"/>), or one lent for
/// the write without being picked (<see cref="Lent"/>), as the web's picker creates a profile as the primary profile and
/// the person still picks who is watching. Both are bound to the account they belong to, and a lent one to its server too:
/// once another account is signed in, or the request is for another server, nothing is sent.
/// </summary>
public sealed class ManagingProfile
{
    private ManagingProfile(Uri? server, string accountId, string profileId, string? token)
    {
        Server = server;
        AccountId = accountId;
        ProfileId = profileId;
        Token = token;
    }

    public string ProfileId { get; }

    /// <summary>The account the profile belongs to; a write goes out only while that account is signed in.</summary>
    public string AccountId { get; }

    /// <summary>The server a lent profile belongs to; null for the acting one, which the session itself binds.</summary>
    public Uri? Server { get; }

    /// <summary>The profile is not the one that acts: its id and proof go with each request instead of the session's.</summary>
    public bool IsLent => Server is not null;

    /// <summary>A lent profile's PIN proof (<c>X-Profile-Token</c>); null for a profile without a PIN. A secret.</summary>
    internal string? Token { get; }

    /// <summary>The profile that acts now for <paramref name="accountId"/>; a write goes out only while it still does.</summary>
    public static ManagingProfile Acting(string accountId, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return new(null, accountId, profileId, null);
    }

    /// <summary>
    /// A profile of <paramref name="accountId"/> on <paramref name="server"/> that does not act, with the proof verify-pin
    /// gave for it (null without a PIN). Both came from the server and go back to it as headers, so anything that could not
    /// be sent back is refused.
    /// </summary>
    /// <exception cref="HttpRequestException">The id or proof cannot be sent as a header.</exception>
    public static ManagingProfile Lent(Uri server, string accountId, string profileId, string? token)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (!SessionCredentials.IsHeaderSafe(profileId) || (token is not null && !SessionCredentials.IsHeaderSafe(token)))
        {
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server sent a profile id or token that cannot be sent back.");
        }
        return new(server, accountId, profileId, token);
    }

    /// <summary>Whether a lent profile belongs to <paramref name="server"/>; the acting one belongs to whichever acts.</summary>
    internal bool BelongsTo(Uri server) => Server is null || ServerAddress.Normalize(Server) == ServerAddress.Normalize(server);

    // Neither the proof nor the ids show in a log or a message.
    public override string ToString() => IsLent ? "ManagingProfile { Lent, Token = <redacted> }" : "ManagingProfile { Acting }";
}
