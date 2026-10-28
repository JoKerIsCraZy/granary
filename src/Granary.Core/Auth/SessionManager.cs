using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Auth;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Auth;

/// <summary>
/// The session state is derived, never kept on the side: it follows the registry's active server and
/// that server's stored credentials. Whatever changes them (sign-in here, a refresh failure in the
/// authentication handler, a removed server) raises <see cref="Changed"/> through the same path.
/// </summary>
public sealed class SessionManager : ISessionManager, IDisposable
{
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(5);

    private readonly IServerRegistry _registry;
    private readonly ICredentialStore _credentials;
    private readonly TokenRefresher _refresher;
    private readonly AuthApi _auth;
    private readonly AccountApi _account;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionManager> _logger;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public SessionManager(
        IServerRegistry registry,
        ICredentialStore credentials,
        TokenRefresher refresher,
        AuthApi auth,
        AccountApi account,
        TimeProvider time,
        ILogger<SessionManager> logger)
    {
        _registry = registry;
        _credentials = credentials;
        _refresher = refresher;
        _auth = auth;
        _account = account;
        _time = time;
        _logger = logger;
        _registry.Changed += OnSourceChanged;
        _credentials.Changed += OnSourceChanged;
    }

    public event EventHandler? Changed;

    public SessionState State => _snapshot.State;

    public ServerEntry? ActiveServer => _snapshot.Server;

    public Account? Account => _snapshot.Account;

    public string? ProfileId => _snapshot.ProfileId;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        await SyncAsync(cancellationToken).ConfigureAwait(false);
        RefreshAccountInBackground();
    }

    public async Task ActivateServerAsync(string serverEntryId, CancellationToken cancellationToken = default)
    {
        await ActivateCoreAsync(serverEntryId, cancellationToken).ConfigureAwait(false);
        RefreshAccountInBackground();
    }

    public async Task<Account> SignInWithPasswordAsync(ServerEntry server, string username, string password, CancellationToken cancellationToken = default)
    {
        var saved = Saved(server);
        var tokens = await _auth.LoginAsync(saved.BaseUri, username, password, cancellationToken).ConfigureAwait(false);
        await SignInWithTokensAsync(saved, tokens, cancellationToken).ConfigureAwait(false);
        return tokens.User;
    }

    public async Task SignInWithTokensAsync(ServerEntry server, TokenPair tokens, CancellationToken cancellationToken = default)
    {
        var saved = Saved(server);
        var credentials = SessionCredentials.Create(tokens, _time.GetUtcNow());
        // Nothing below is cancelled: the server has already created the session.
        if (await _credentials.GetAsync(saved.Id, CancellationToken.None).ConfigureAwait(false) is not null)
        {
            // Signing in again replaces a login; end the old one on the server instead of leaving it behind.
            await LogoutAsync(saved, CancellationToken.None).ConfigureAwait(false);
        }
        await _credentials.SetAsync(saved.Id, credentials, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Signed in to server {ServerEntryId}", saved.Id);
        await ActivateCoreAsync(saved.Id, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<bool> ReopenServerAsync(string serverEntryId, string? serverId, CancellationToken cancellationToken = default)
    {
        var entry = _registry.Find(serverEntryId) ?? throw new InvalidOperationException("The server is not saved.");
        if (entry.ServerId is { } saved && !string.Equals(saved, serverId, StringComparison.Ordinal))
        {
            if (serverId is not null && await _credentials.GetAsync(entry.Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                // Another server answers at this address now. Signing out would send it the old tokens: they are
                // forgotten here only.
                _logger.LogWarning("Saved server {ServerEntryId} answers as another server; its session is forgotten", entry.Id);
                await _credentials.RemoveAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
                await SyncAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return false;
        }
        // Who is watching is asked again; the login stays. The profile goes first, so the shell never shows the old
        // profile's Home on the way to the picker.
        if (await _credentials.UpdateAsync(entry.Id, c => c with { ProfileId = null, ProfileToken = null }, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }
        _logger.LogInformation("Opened saved server {ServerEntryId} again", entry.Id);
        // Not cancelled once the profile is gone: the server becomes active rather than being left half reopened.
        await ActivateCoreAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
        RefreshAccountInBackground();
        return true;
    }

    public async Task SelectProfileAsync(string serverEntryId, string profileId, string? profileToken, CancellationToken cancellationToken = default)
    {
        // Both come from the server and go back to it as headers.
        if (!SessionCredentials.IsHeaderSafe(profileId) || (profileToken is not null && !SessionCredentials.IsHeaderSafe(profileToken)))
        {
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server sent a profile id or token that cannot be sent back.");
        }
        // A profile of one server's account means nothing to another: the pick needs the server it was listed for.
        var server = RequireActiveServer();
        if (server.Id != serverEntryId || _registry.ActiveServerId != serverEntryId)
        {
            throw new ActiveServerChangedException("The profile was picked for a server that is no longer active.");
        }
        var updated = await _credentials.UpdateAsync(server.Id,
            c => c with { ProfileId = profileId, ProfileToken = profileToken }, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selecting a profile needs a signed-in server.");
        if (_registry.Find(server.Id) is { } entry && entry.LastProfileId != profileId)
        {
            await _registry.UpdateAsync(entry with { LastProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        }
        _logger.LogInformation("Selected profile {ProfileId} on server {ServerEntryId}", updated.ProfileId, server.Id);
        await SyncAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearProfileAsync(CancellationToken cancellationToken = default)
    {
        var server = RequireActiveServer();
        await _credentials.UpdateAsync(server.Id, c => c with { ProfileId = null, ProfileToken = null }, cancellationToken).ConfigureAwait(false);
        await SyncAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAccountAsync(CancellationToken cancellationToken = default)
    {
        if (_snapshot.Server is not { } server
            || await _credentials.GetAsync(server.Id, cancellationToken).ConfigureAwait(false) is not { } session)
        {
            return;
        }
        if (session.Account?.PasswordChangeRequired == true)
        {
            // The restriction is part of the old tokens; new ones drop it once the password was changed.
            var result = await _refresher.RefreshAsync(server, session, cancellationToken).ConfigureAwait(false);
            switch (result)
            {
                case { Status: RefreshStatus.SignedOut or RefreshStatus.Superseded }:
                    return;
                case { Status: RefreshStatus.Failed, Error: { } error }:
                    throw error;
            }
        }
        var me = await _account.GetMeAsync(server.BaseUri, cancellationToken).ConfigureAwait(false);
        await _credentials.UpdateAsync(server.Id, session.SessionId, c => c with { Account = me }, CancellationToken.None).ConfigureAwait(false);
        await SyncAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task SignOutAsync(string? serverEntryId = null, CancellationToken cancellationToken = default)
    {
        string? id = serverEntryId ?? _registry.ActiveServerId;
        if (id is null)
        {
            return;
        }
        try
        {
            if (_registry.Find(id) is { } server && await _credentials.GetAsync(id, cancellationToken).ConfigureAwait(false) is not null)
            {
                await LogoutAsync(server, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Local credentials go even when the server could not be told, or the caller gave up waiting.
            await _credentials.RemoveAsync(id, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Signed out of server {ServerEntryId}", id);
            await SyncAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _registry.Changed -= OnSourceChanged;
        _credentials.Changed -= OnSourceChanged;
        _syncGate.Dispose();
    }

    private async Task LogoutAsync(ServerEntry server, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LogoutTimeout);
        try
        {
            await _auth.LogoutAsync(server.BaseUri, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Server {ServerEntryId} could not be told about the sign-out", server.Id);
        }
    }

    private async Task ActivateCoreAsync(string serverEntryId, CancellationToken cancellationToken)
    {
        var entry = _registry.Find(serverEntryId)
            ?? throw new InvalidOperationException("The server is not saved.");
        await _registry.UpdateAsync(entry with { LastUsedAt = _time.GetUtcNow() }, cancellationToken).ConfigureAwait(false);
        await _registry.SetActiveAsync(serverEntryId, cancellationToken).ConfigureAwait(false);
        await SyncAsync(cancellationToken).ConfigureAwait(false);
    }

    // The registry's entry, not the caller's copy, decides where credentials go.
    private ServerEntry Saved(ServerEntry server) =>
        _registry.Find(server.Id) ?? throw new InvalidOperationException("Sign-in needs a saved server.");

    private ServerEntry RequireActiveServer() =>
        _snapshot.Server ?? throw new InvalidOperationException("No server is active.");

    // Checks the session against the server in the background, so start-up never waits on the network.
    // A revoked session surfaces here as a sign-out through the authentication handler.
    private void RefreshAccountInBackground()
    {
        if (State is SessionState.NoServer or SessionState.SignedOut)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAccountAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or SiloApiException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not refresh the account; keeping the stored one");
            }
        });
    }

    private void OnSourceChanged(object? sender, EventArgs e) => SyncInBackground();

    private void OnSourceChanged(object? sender, string serverEntryId) => SyncInBackground();

    private void SyncInBackground() =>
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not update the session state");
            }
        });

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        bool changed;
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var server = _registry.Active;
            var session = server is null ? null : await _credentials.GetAsync(server.Id, cancellationToken).ConfigureAwait(false);
            var next = Snapshot.From(server, session);
            changed = next != _snapshot;
            _snapshot = next;
        }
        finally
        {
            _syncGate.Release();
        }
        if (changed)
        {
            _logger.LogDebug("Session state is now {State}", State);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed record Snapshot(SessionState State, ServerEntry? Server, Account? Account, string? ProfileId)
    {
        public static readonly Snapshot Empty = new(SessionState.NoServer, null, null, null);

        public static Snapshot From(ServerEntry? server, SessionCredentials? session)
        {
            if (server is null)
            {
                return Empty;
            }
            if (session is null)
            {
                return new Snapshot(SessionState.SignedOut, server, null, null);
            }
            var state = session.Account?.PasswordChangeRequired == true ? SessionState.PasswordChangeRequired
                : session.ProfileId is null ? SessionState.ProfileRequired
                : SessionState.Ready;
            return new Snapshot(state, server, session.Account, session.ProfileId);
        }
    }
}
