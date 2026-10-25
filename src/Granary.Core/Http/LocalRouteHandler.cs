using Granary.Core.Api;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Http;

/// <summary>
/// Sends a saved server's requests over its local route while one is verified (docs/api-contract.md, 9.7): the same
/// request, headers and all, to the private address, over TLS that accepts only the pinned key. As Android does, every
/// request takes it but sign-in, token refresh and sign-out (<c>/api/v2/auth/</c>): the server keys what it knows of a
/// device's connection (where a start plays from, speed readings, learned limits) by the path a request came over, so
/// writes must come the way the media goes. A route that does not connect is dropped, and the request goes to the saved
/// address after all; that is only repeated when no connection was made, so it cannot have reached the server. It sits
/// after the authentication handler, which decides by the saved address, and in front of the media proxy's way
/// upstream.
/// </summary>
public sealed class LocalRouteHandler(LocalRoutes routes, ILogger<LocalRouteHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } saved
            || IsSignIn(saved)
            || (request.Options.TryGetValue(SiloRequestOptions.SavedAddressOnly, out bool savedOnly) && savedOnly)
            || routes.Find(saved) is not { } route)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        request.RequestUri = route.Rewrite(saved);
        try
        {
            return await route.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (DidNotConnect(ex, cancellationToken))
        {
            logger.LogInformation("The local route to {Server} did not connect ({Failure}); using the saved address",
                route.SavedOrigin.Host, ex is HttpRequestException http ? http.HttpRequestError.ToString() : ex.GetType().Name);
            routes.Drop(route);
        }
        finally
        {
            // A replay (the authentication handler's after a refresh) starts from the saved address again.
            request.RequestUri = saved;
        }
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // A route only qualifies for a saved address without a path prefix, so the API sits at the root.
    private static bool IsSignIn(Uri uri) => uri.IsAbsoluteUri && uri.AbsolutePath.StartsWith("/api/v2/auth/", StringComparison.OrdinalIgnoreCase);

    // The route is not there for this request: no connection, a key other than the pinned one, a connect that timed out,
    // or a route replaced meanwhile. Repeating it on the saved address is harmless either way.
    private static bool DidNotConnect(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpRequestException http => http.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
            or HttpRequestError.NameResolutionError,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        ObjectDisposedException => true,
        _ => false,
    };
}
