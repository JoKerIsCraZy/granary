using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>Serves streams to the player through the loopback, with the server's credentials added on the way.</summary>
public interface IMediaProxy
{
    /// <summary>
    /// Serves <paramref name="upstream"/> (with <see cref="MediaRouteScope.Directory"/> also what lies beside it) until the
    /// route is disposed. <paramref name="initial"/>, when known, saves the first request asking for headers.
    /// </summary>
    IMediaRoute Open(Uri upstream, MediaRouteScope scope, MediaCredentialSource credentials, MediaCredentials? initial = null);
}

/// <summary>
/// An HTTP server on 127.0.0.1 between mpv and the server (docs/api-contract.md, 9.4; Apple's client works the same
/// way). mpv gets no credential: it would keep them for the whole load, send them wherever a redirect or a playlist
/// points, and could not renew an expired one. The proxy adds the headers per request, only towards the route's own
/// stream, and never follows a redirect. Its URLs carry a random secret, so other programs on the machine cannot use
/// it to reach the server as the viewer.
/// </summary>
public sealed class MediaProxy : IMediaProxy, IDisposable
{
    private readonly HttpMessageInvoker _client;
    private readonly TimeProvider _time;
    private readonly ILogger<MediaProxy> _logger;
    private readonly ConcurrentDictionary<string, MediaRoute> _routes = new(StringComparer.Ordinal);
    private readonly string _secret = RandomToken(32);
    private readonly Lock _gate = new();
    private HttpListener? _listener;
    private string _root = "";
    private bool _disposed;

    public MediaProxy(HttpMessageHandler upstream, TimeProvider time, ILogger<MediaProxy> logger)
    {
        _client = new HttpMessageInvoker(upstream);
        _time = time;
        _logger = logger;
    }

    public IMediaRoute Open(Uri upstream, MediaRouteScope scope, MediaCredentialSource credentials, MediaCredentials? initial = null)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(credentials);
        if (!upstream.IsAbsoluteUri || upstream.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("The proxy serves HTTP streams only.", nameof(upstream));
        }
        string root = EnsureListening();
        string id = RandomToken(16);
        var route = new MediaRoute(this, id, root + "/" + id, upstream, scope, credentials, initial, _time);
        _routes[id] = route;
        return route;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _listener?.Close();
        }
        foreach (var route in _routes.Values)
        {
            route.Dispose();
        }
        _client.Dispose();
    }

    internal void Remove(MediaRoute route) => _routes.TryRemove(new KeyValuePair<string, MediaRoute>(route.Id, route));

    private string EnsureListening()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is null)
            {
                (_listener, int port) = Listen();
                _root = $"http://127.0.0.1:{port}/{_secret}";
                _logger.LogInformation("Media proxy listening on the loopback, port {Port}", port);
                _ = AcceptAsync(_listener);
            }
            return _root;
        }
    }

    // A free port as the system hands it out; another program can take it in between, so a few tries.
    private static (HttpListener Listener, int Port) Listen()
    {
        for (int tries = 1; ; tries++)
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException) when (tries < 5)
            {
                listener.Close();
            }
        }
    }

    private async Task AcceptAsync(HttpListener listener)
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = ServeAsync(context);
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            if (context.Request.HttpMethod is not ("GET" or "HEAD"))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }
            if (Find(context.Request.RawUrl) is not { } found)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            await found.Route.ForwardAsync(context, found.Upstream, _client, _logger).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation("The server could not be reached for a media request: {Failure}", ex.HttpRequestError);
            TrySetStatus(response, HttpStatusCode.BadGateway);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException)
        {
            // The player went away (a seek, a new load), the route closed, or the upstream body broke off.
            _logger.LogDebug("A media request ended early: {Kind}", ex.GetType().Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A media request failed unexpectedly");
            TrySetStatus(response, HttpStatusCode.InternalServerError);
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Aborted or already gone.
            }
        }
    }

    // /{secret}/{route}/{the upstream path and query, as the player resolved it}
    private (MediaRoute Route, Uri Upstream)? Find(string? rawUrl)
    {
        if (rawUrl is null || rawUrl.Length < _secret.Length + 2 || rawUrl[0] != '/')
        {
            return null;
        }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(rawUrl.AsSpan(1, _secret.Length).ToString()), Encoding.ASCII.GetBytes(_secret))
            || rawUrl[_secret.Length + 1] != '/')
        {
            return null;
        }
        string rest = rawUrl[(_secret.Length + 2)..];
        int slash = rest.IndexOf('/');
        if (slash <= 0 || !_routes.TryGetValue(rest[..slash], out var route))
        {
            return null;
        }
        return route.Map(rest[slash..]) is { } upstream ? (route, upstream) : null;
    }

    private static void TrySetStatus(HttpListenerResponse response, HttpStatusCode status)
    {
        try
        {
            response.StatusCode = (int)status;
        }
        catch (InvalidOperationException)
        {
            // The headers are out already; closing ends the response.
        }
    }

    private static string RandomToken(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
}
