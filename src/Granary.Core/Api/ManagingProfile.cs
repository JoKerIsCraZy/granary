using Granary.Core.Auth;

namespace Granary.Core.Api;

/// <summary>
/// The profile a household write goes out as: the server lets only the household's verified primary profile create, change
/// and delete profiles (docs/api-contract.md, 6.7). Either the profile that acts now (<see cref="Acting"/>), or one lent for
/// the write without being picked (<see cref="Lent"/>), as the web's picker creates a profile as the primary profile and
/// the person still picks who is watching.
/// </summary>
public sealed class ManagingProfile
{
    private ManagingProfile(string profileId, bool isLent, string? token)
    {
        ProfileId = profileId;
        IsLent = isLent;
        Token = token;
    }

    public string ProfileId { get; }

    /// <summary>The profile is not the one that acts: its id and proof go with each request instead of the session's.</summary>
    public bool IsLent { get; }

    /// <summary>A lent profile's PIN proof (<c>X-Profile-Token</c>); null for a profile without a PIN. A secret.</summary>
    internal string? Token { get; }

    /// <summary>The profile that acts now; a write goes out only while it still does.</summary>
    public static ManagingProfile Acting(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return new(profileId, false, null);
    }

    /// <summary>
    /// A profile that does not act, with the proof verify-pin gave for it (null without a PIN). Both came from the server and
    /// go back to it as headers, so anything that could not be sent back is refused.
    /// </summary>
    /// <exception cref="HttpRequestException">The id or proof cannot be sent as a header.</exception>
    public static ManagingProfile Lent(string profileId, string? token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (!SessionCredentials.IsHeaderSafe(profileId) || (token is not null && !SessionCredentials.IsHeaderSafe(token)))
        {
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server sent a profile id or token that cannot be sent back.");
        }
        return new(profileId, true, token);
    }

    // The proof never shows in a log or a message.
    public override string ToString() => IsLent ? "ManagingProfile { Lent, Token = <redacted> }" : "ManagingProfile { Acting }";
}
