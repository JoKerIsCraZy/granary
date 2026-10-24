using Granary.Core.Api.Auth;
using Granary.Core.Servers;

namespace Granary.Core.Auth;

/// <summary>Where the user stands with the active server; the shell routes on it.</summary>
public enum SessionState
{
    /// <summary>No server saved or active.</summary>
    NoServer,

    /// <summary>A server is active but has no login session.</summary>
    SignedOut,

    /// <summary>Signed in with a temporary password that must be changed first.</summary>
    PasswordChangeRequired,

    /// <summary>Signed in; no profile selected yet.</summary>
    ProfileRequired,

    /// <summary>Signed in with a profile; the app is usable.</summary>
    Ready,
}

/// <summary>
/// Owns the login session of the active server: tokens, account and selected profile. View models
/// call it; the shell navigates on <see cref="Changed"/>. Tokens are never exposed to view models.
/// </summary>
public interface ISessionManager
{
    SessionState State { get; }

    ServerEntry? ActiveServer { get; }

    Account? Account { get; }

    string? ProfileId { get; }

    /// <summary>Raised after every change of <see cref="State"/>, server, account or profile.</summary>
    event EventHandler? Changed;

    /// <summary>Loads the registry and the active server's saved session; call once at startup.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes a saved server active and loads its saved session, if any.</summary>
    Task ActivateServerAsync(string serverEntryId, CancellationToken cancellationToken = default);

    /// <summary>Password sign-in on a saved server; activates it on success.</summary>
    Task<Account> SignInWithPasswordAsync(ServerEntry server, string username, string password, CancellationToken cancellationToken = default);

    /// <summary>Completes a sign-in whose tokens came from elsewhere (device sign-in); activates the server.</summary>
    Task SignInWithTokensAsync(ServerEntry server, TokenPair tokens, CancellationToken cancellationToken = default);

    /// <summary>Selects the acting profile; <paramref name="profileToken"/> is required for a PIN profile.</summary>
    Task SelectProfileAsync(string profileId, string? profileToken, CancellationToken cancellationToken = default);

    /// <summary>Forgets the selected profile but keeps the login session (back to the profile picker).</summary>
    Task ClearProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>Refreshes the tokens and re-reads the account, for example after a password change.</summary>
    Task RefreshAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>Revokes and forgets the session of a saved server (the active one when null); keeps the entry.</summary>
    Task SignOutAsync(string? serverEntryId = null, CancellationToken cancellationToken = default);
}
