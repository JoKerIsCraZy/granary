using System.Collections.Concurrent;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Auth;

public enum RefreshStatus
{
    /// <summary><see cref="RefreshResult.Credentials"/> hold a newer access token of the same login.</summary>
    Refreshed,

    /// <summary>The server ended the session; the credentials are gone.</summary>
    SignedOut,

    /// <summary>Another login replaced the stale one meanwhile; nothing of the old login may be retried.</summary>
    Superseded,

    /// <summary>The refresh failed for now (<see cref="RefreshResult.Error"/>); the credentials are kept.</summary>
    Failed,
}

public sealed record RefreshResult(RefreshStatus Status, SessionCredentials? Credentials = null, SiloApiException? Error = null);

/// <summary>
/// Refreshes a server's tokens at most once at a time (docs/api-contract.md, 5.2). Callers pass the
/// credentials they found stale; when another caller has replaced the access token in the meantime, they
/// get the newer credentials without a network call. Results only ever apply to the stale login.
/// </summary>
public sealed class TokenRefresher(
    ICredentialStore credentials,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<TokenRefresher> logger)
{
    /// <summary>The refresh client has the identity headers but not the authentication handler, which calls back here.</summary>
    public const string HttpClientName = "Granary.TokenRefresh";

    // The refresh itself is not tied to the caller's cancellation: a refresh that succeeded on the
    // server must be stored, or a server with strict rotation would have invalidated our refresh token.
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);

    // How long a failed refresh answers later callers with the same token, unless Retry-After says otherwise.
    private static readonly TimeSpan FailureHold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxFailureHold = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string AccessToken, RefreshResult Result, DateTimeOffset Until)> _recentFailures =
        new(StringComparer.Ordinal);

    /// <summary>Cancelling stops the wait, not a refresh already sent.</summary>
    public Task<RefreshResult> RefreshAsync(ServerEntry server, SessionCredentials stale, CancellationToken cancellationToken = default) =>
        RefreshGatedAsync(server, stale).WaitAsync(cancellationToken);

    private async Task<RefreshResult> RefreshGatedAsync(ServerEntry server, SessionCredentials stale)
    {
        var gate = _gates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = await credentials.GetAsync(server.Id).ConfigureAwait(false);
            if (current is null)
            {
                return new RefreshResult(RefreshStatus.SignedOut);
            }
            if (current.SessionId != stale.SessionId)
            {
                return new RefreshResult(RefreshStatus.Superseded);
            }
            if (!string.Equals(current.AccessToken, stale.AccessToken, StringComparison.Ordinal))
            {
                return new RefreshResult(RefreshStatus.Refreshed, current);
            }
            if (_recentFailures.TryGetValue(server.Id, out var failure)
                && failure.AccessToken == stale.AccessToken
                && timeProvider.GetUtcNow() < failure.Until)
            {
                return failure.Result;
            }
            var result = await RefreshCoreAsync(server, current).ConfigureAwait(false);
            if (result is { Status: RefreshStatus.Failed, Error: { } error })
            {
                var hold = error.RetryAfter is { } retryAfter ? (retryAfter < MaxFailureHold ? retryAfter : MaxFailureHold) : FailureHold;
                _recentFailures[server.Id] = (stale.AccessToken, result, timeProvider.GetUtcNow() + hold);
            }
            else
            {
                _recentFailures.TryRemove(server.Id, out _);
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RefreshResult> RefreshCoreAsync(ServerEntry server, SessionCredentials current)
    {
        using var timeout = new CancellationTokenSource(RefreshTimeout, timeProvider);
        var api = new AuthApi(new SiloHttp(httpClientFactory.CreateClient(HttpClientName)));
        try
        {
            var tokens = await api.RefreshAsync(server.BaseUri, current.RefreshToken, timeout.Token).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var updated = await credentials.UpdateAsync(server.Id, current.SessionId, c => c.WithRefreshedTokens(tokens, now), CancellationToken.None)
                .ConfigureAwait(false);
            if (updated is null)
            {
                // Signed out, or signed in again, while the refresh was in flight.
                return new RefreshResult(RefreshStatus.Superseded);
            }
            logger.LogInformation("Refreshed the session of server {ServerEntryId}", server.Id);
            return new RefreshResult(RefreshStatus.Refreshed, updated);
        }
        catch (SiloApiException ex) when (ex.Status is 400 or 401 or 403)
        {
            logger.LogWarning("Server {ServerEntryId} rejected the refresh with {Status} {Code}; signing out", server.Id, ex.Status, ex.Code);
            await credentials.RemoveAsync(server.Id, current.SessionId, CancellationToken.None).ConfigureAwait(false);
            return new RefreshResult(RefreshStatus.SignedOut);
        }
        catch (SiloApiException ex) when (ex.Status == 422)
        {
            // A client bug, not a dead session: neither reference client signs out here.
            logger.LogError("Server {ServerEntryId} rejected the refresh request as invalid ({Code}); keeping the session", server.Id, ex.Code);
            return new RefreshResult(RefreshStatus.Failed, Error: ex);
        }
        catch (SiloApiException ex)
        {
            logger.LogWarning("Refresh for server {ServerEntryId} failed with {Status} {Code}; keeping the session", server.Id, ex.Status, ex.Code);
            return new RefreshResult(RefreshStatus.Failed, Error: ex);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            // Surface like any other unreachable server, not as a cancellation nobody asked for.
            throw new HttpRequestException(HttpRequestError.ConnectionError, "The token refresh timed out.", ex);
        }
    }
}
