using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Granary.Core.Api;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Http;

/// <summary>
/// Puts a saved server's bearer token, and for <see cref="SiloAuth.Profile"/> requests its profile
/// headers, on requests beneath that server's base URL, and keeps the session alive the way Android's
/// SiloAuthPlugin does (docs/api-contract.md, 5.2): refresh shortly before expiry, refresh once on a
/// 401 and replay the request, sign out when the server ends the session, and drop the profile when the
/// server asks for its PIN again. Every consequence applies only to the login the request was sent with.
/// </summary>
public sealed class AuthenticationHandler(
    IServerRegistry registry,
    ICredentialStore credentials,
    TokenRefresher refresher,
    TimeProvider timeProvider,
    ILogger<AuthenticationHandler> logger) : DelegatingHandler
{
    internal const string ProfileIdHeader = "X-Profile-Id";
    internal const string ProfileTokenHeader = "X-Profile-Token";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var auth = request.GetAuth();
        var server = auth == SiloAuth.Anonymous ? null : FindServer(request);
        var session = server is null ? null : await credentials.GetAsync(server.Id, cancellationToken).ConfigureAwait(false);
        if (session is not null && session.ShouldRefresh(timeProvider.GetUtcNow()))
        {
            session = await RefreshAheadAsync(server!, session, cancellationToken).ConfigureAwait(false);
        }
        if (server is null || session is null)
        {
            // Without credentials the server answers 401, and the caller gets exactly that.
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.Content is not null)
        {
            // A replay after a refresh sends the body again.
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        Authorize(request, session, auth);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return await AfterResponseAsync(response, server, session, auth, cancellationToken).ConfigureAwait(false);
        }

        string code = await ProblemCodeAsync(response, cancellationToken).ConfigureAwait(false);
        if (code == ProblemCodes.SessionExpired)
        {
            return await AfterResponseAsync(response, server, session, auth, cancellationToken).ConfigureAwait(false);
        }
        RefreshResult result;
        try
        {
            result = await refresher.RefreshAsync(server, session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        switch (result.Status)
        {
            case RefreshStatus.Refreshed:
                // A 401 comes from the auth middleware before the handler ran, so replaying is safe. Once only,
                // as the same login and profile: only the bearer token changes.
                response.Dispose();
                var retry = Clone(request);
                retry.Headers.Authorization = Bearer(result.Credentials!);
                var retried = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
                return await AfterResponseAsync(retried, server, result.Credentials!, auth, cancellationToken).ConfigureAwait(false);
            case RefreshStatus.Failed when result.Error is { Status: not 422 } error:
                // Like the Apple client: the caller learns the server is unavailable, not that it was unauthorized.
                response.Dispose();
                return ToResponse(error, request);
            default:
                return response;
        }
    }

    private ServerEntry? FindServer(HttpRequestMessage request)
    {
        if (request.GetServer() is not { } target)
        {
            return null;
        }
        var normalized = ServerAddress.Normalize(target);
        return registry.Servers.FirstOrDefault(s => ServerAddress.Normalize(s.BaseUri) == normalized);
    }

    private async Task<SessionCredentials?> RefreshAheadAsync(ServerEntry server, SessionCredentials session, CancellationToken cancellationToken)
    {
        try
        {
            var result = await refresher.RefreshAsync(server, session, cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                RefreshStatus.Refreshed => result.Credentials,
                RefreshStatus.SignedOut => null,
                // A newer login exists; this request starts fresh with it.
                RefreshStatus.Superseded => await credentials.GetAsync(server.Id, cancellationToken).ConfigureAwait(false),
                // The old token still has up to a minute left; let the request try it.
                _ => session,
            };
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Early refresh for server {ServerEntryId} failed; trying the current token", server.Id);
            return session;
        }
    }

    /// <summary>What a final answer means for the login it was sent with: an ended session or a rejected profile.</summary>
    private async Task<HttpResponseMessage> AfterResponseAsync(
        HttpResponseMessage response, ServerEntry server, SessionCredentials session, SiloAuth auth, CancellationToken cancellationToken)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized
                when await ProblemCodeAsync(response, cancellationToken).ConfigureAwait(false) == ProblemCodes.SessionExpired:
                logger.LogWarning("Server {ServerEntryId} ended the session; signing out", server.Id);
                await credentials.RemoveAsync(server.Id, session.SessionId, CancellationToken.None).ConfigureAwait(false);
                break;
            case HttpStatusCode.Forbidden when auth == SiloAuth.Profile && session.ProfileId is { } profileId
                && await ProblemCodeAsync(response, cancellationToken).ConfigureAwait(false) == ProblemCodes.ProfileVerificationRequired:
                logger.LogInformation("Server {ServerEntryId} wants the profile verified again; clearing the selection", server.Id);
                // Only if it is still the profile and PIN token this request acted with; the user may have re-verified meanwhile.
                await credentials.UpdateAsync(server.Id, session.SessionId,
                    c => c.ProfileId == profileId && c.ProfileToken == session.ProfileToken ? c with { ProfileId = null, ProfileToken = null } : c,
                    CancellationToken.None).ConfigureAwait(false);
                break;
        }
        return response;
    }

    private static void Authorize(HttpRequestMessage request, SessionCredentials session, SiloAuth auth)
    {
        request.Headers.Authorization = Bearer(session);
        request.Headers.Remove(ProfileIdHeader);
        request.Headers.Remove(ProfileTokenHeader);
        if (auth == SiloAuth.Profile && session.ProfileId is { } profileId)
        {
            request.Headers.TryAddWithoutValidation(ProfileIdHeader, profileId);
            if (session.ProfileToken is { } profileToken)
            {
                request.Headers.TryAddWithoutValidation(ProfileTokenHeader, profileToken);
            }
        }
    }

    private static AuthenticationHeaderValue Bearer(SessionCredentials session) => new("Bearer", session.AccessToken);

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Content = request.Content,
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }
        return clone;
    }

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Buffered, so the caller can still read the body.
        await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        var problem = await SiloHttp.ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
        return problem?.Code ?? ProblemCodes.Unknown;
    }

    private static HttpResponseMessage ToResponse(SiloApiException error, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage((HttpStatusCode)error.Status)
        {
            RequestMessage = request,
            Content = JsonContent.Create(error.Problem, SiloJsonContext.Default.ApiProblem, new MediaTypeHeaderValue("application/problem+json")),
        };
        if (error.RetryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }
        return response;
    }
}
