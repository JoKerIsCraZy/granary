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
        EnsureSaved(server);
        var tokens = await _auth.LoginAsync(server.BaseUri, username, password, cancellationToken).ConfigureAwait(false);
        await SignInWithTokensAsync(server, tokens, cancellationToken).ConfigureAwait(false);
        return tokens.User;
    }

    public async Task SignInWithTokensAsync(ServerEntry server, TokenPair tokens, CancellationToken cancellationToken = default)
    {
        EnsureSaved(server);
        // Stored before anything can be cancelled: the server has already created the session.
        await _credentials.SetAsync(server.Id, SessionCredentials.Create(tokens, _time.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Signed in to server {ServerEntryId}", server.Id);
        await ActivateCoreAsync(server.Id, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task SelectProfileAsync(string profileId, string? profileToken, CancellationToken cancellationToken = default)
    {
        var server = RequireActiveServer();
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
            var result = await _refresher.RefreshAsync(server, session.AccessToken, cancellationToken).ConfigureAwait(false);
            switch (result)
            {
                case { Status: RefreshStatus.SignedOut }:
                    return;
                case { Status: RefreshStatus.Failed, Error: { } error }:
                    throw error;
            }
        }
        var me = await _account.GetMeAsync(server.BaseUri, cancellationToken).ConfigureAwait(false);
        await _credentials.UpdateAsync(server.Id, c => c with { Account = me }, CancellationToken.None).ConfigureAwait(false);
        await SyncAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task SignOutAsync(string? serverEntryId = null, CancellationToken cancellationToken = default)
    {
        string? id = serverEntryId ?? _registry.ActiveServerId;
        if (id is null)
        {
            return;
        }
        if (_registry.Find(id) is { } server && await _credentials.GetAsync(id, cancellationToken).ConfigureAwait(false) is not null)
        {
            await LogoutAsync(server, cancellationToken).ConfigureAwait(false);
        }
        // Local credentials go even when the server could not be told.
        await _credentials.RemoveAsync(id, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Signed out of server {ServerEntryId}", id);
        await SyncAsync(CancellationToken.None).ConfigureAwait(false);
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

    private void EnsureSaved(ServerEntry server)
    {
        if (_registry.Find(server.Id) is null)
        {
            throw new InvalidOperationException("Sign-in needs a saved server.");
        }
    }

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
