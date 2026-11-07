using Granary.Core.Auth;

namespace Granary.Core.Downloads;

/// <summary>
/// Whose downloads these are: a saved server's entry, the account signed in there and the profile. The server keeps each
/// download for one account, profile and PC, so Granary keeps them apart the same way and lists only the active scope's.
/// </summary>
public sealed record DownloadScope(string ServerEntryId, string AccountId, string ProfileId)
{
    /// <summary>The session's scope; null unless it is ready with a server, an account and a profile.</summary>
    public static DownloadScope? Of(ISessionManager session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.State == SessionState.Ready && session.ActiveServer is { } server && session.Account is { } account
            && session.ProfileId is { Length: > 0 } profile
            ? new DownloadScope(server.Id, account.Id, profile)
            : null;
    }
}
