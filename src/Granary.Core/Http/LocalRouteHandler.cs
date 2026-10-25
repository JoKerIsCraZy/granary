using Granary.Core.Api;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Http;

/// <summary>
/// Sends requests for a saved server over its local route while one is verified (docs/api-contract.md, 9.7): the
/// same request, headers and all, to the private address, over TLS that accepts only the pinned key. A route that
/// does not connect is dropped, and the request goes to the saved address after all. It sits after the
/// authentication handler, which decides by the saved address, and in front of the media proxy's way upstream.
/// </summary>
public sealed class LocalRouteHandler(LocalRoutes routes, ILogger<LocalRouteHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } saved
            || (request.Options.TryGetValue(SiloRequestOptions.SavedAddressOnly, out bool savedOnly) && savedOnly)
            || routes.Find(saved) is not { } route)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        if (request.Content is not null)
        {
            // Kept, so the saved address can get the same body if the route does not connect.
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
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

    // Nothing reached the server: no connection, a key other than the pinned one, a connect that timed out, or a route
    // replaced meanwhile. Anything later may have been sent, and is not repeated.
    private static bool DidNotConnect(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpRequestException http => http.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
            or HttpRequestError.NameResolutionError,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        ObjectDisposedException => true,
        _ => false,
    };
}
