namespace Granary.Core.Auth;

/// <summary>Per-server <see cref="SessionCredentials"/>. All changes go through here, so the session follows them.</summary>
public interface ICredentialStore
{
    /// <summary>Raised after a server's credentials changed or were removed; the argument is the server entry id.</summary>
    event EventHandler<string>? Changed;

    Task<SessionCredentials?> GetAsync(string serverEntryId, CancellationToken cancellationToken = default);

    Task SetAsync(string serverEntryId, SessionCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces the stored credentials with <paramref name="update"/> applied to them, so
    /// concurrent writers (a token refresh, a profile switch) never undo each other. Returns null when the
    /// server has no credentials.
    /// </summary>
    Task<SessionCredentials?> UpdateAsync(string serverEntryId, Func<SessionCredentials, SessionCredentials> update, CancellationToken cancellationToken = default);

    Task RemoveAsync(string serverEntryId, CancellationToken cancellationToken = default);
}
