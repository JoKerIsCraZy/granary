using System.Collections.Concurrent;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Auth;

public enum RefreshStatus
{
    /// <summary><see cref="RefreshResult.Credentials"/> hold a newer access token than the stale one.</summary>
    Refreshed,

    /// <summary>The server ended the session; the credentials are gone.</summary>
    SignedOut,

    /// <summary>The refresh failed for now (<see cref="RefreshResult.Error"/>); the credentials are kept.</summary>
    Failed,
}

public sealed record RefreshResult(RefreshStatus Status, SessionCredentials? Credentials = null, SiloApiException? Error = null);

/// <summary>
/// Refreshes a server's tokens at most once at a time (docs/api-contract.md, 5.2). Callers pass the
/// access token they found stale; when another caller has replaced it in the meantime, they get the
/// newer credentials without a network call.
/// </summary>
public sealed class TokenRefresher(
    ICredentialStore credentials,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<TokenRefresher> logger)
{
    /// <summary>The refresh client has the identity headers but not the authentication handler, which calls back here.</summary>
    public const string HttpClientName = "Granary.TokenRefresh";

    // Not tied to the caller's cancellation: a refresh that succeeded on the server must be stored, or a
    // server with strict rotation would have invalidated the refresh token we still hold.
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<RefreshResult> RefreshAsync(ServerEntry server, string staleAccessToken, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await credentials.GetAsync(server.Id, CancellationToken.None).ConfigureAwait(false);
            if (current is null)
            {
                return new RefreshResult(RefreshStatus.SignedOut);
            }
            if (!string.Equals(current.AccessToken, staleAccessToken, StringComparison.Ordinal))
            {
                return new RefreshResult(RefreshStatus.Refreshed, current);
            }
            return await RefreshCoreAsync(server, current.RefreshToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RefreshResult> RefreshCoreAsync(ServerEntry server, string refreshToken)
    {
        using var timeout = new CancellationTokenSource(RefreshTimeout, timeProvider);
        var api = new AuthApi(new SiloHttp(httpClientFactory.CreateClient(HttpClientName)));
        try
        {
            var tokens = await api.RefreshAsync(server.BaseUri, refreshToken, timeout.Token).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var updated = await credentials.UpdateAsync(server.Id, c => c.WithRefreshedTokens(tokens, now), CancellationToken.None)
                .ConfigureAwait(false);
            if (updated is null)
            {
                // Signed out while the refresh was in flight.
                return new RefreshResult(RefreshStatus.SignedOut);
            }
            logger.LogInformation("Refreshed the session of server {ServerEntryId}", server.Id);
            return new RefreshResult(RefreshStatus.Refreshed, updated);
        }
        catch (SiloApiException ex) when (ex.Status is 400 or 401 or 403)
        {
            logger.LogWarning("Server {ServerEntryId} rejected the refresh with {Status} {Code}; signing out", server.Id, ex.Status, ex.Code);
            await credentials.RemoveAsync(server.Id, CancellationToken.None).ConfigureAwait(false);
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
