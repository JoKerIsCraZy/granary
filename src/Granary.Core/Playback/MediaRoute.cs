using System.Net;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>Where a route may reach upstream.</summary>
public enum MediaRouteScope
{
    /// <summary>The stream's URL only, query included: a progressive stream.</summary>
    File,

    /// <summary>Also what lies beside it: an HLS playlist's variants and segments.</summary>
    Directory,
}

/// <summary>The headers for upstream requests; <paramref name="rejected"/>: the server refused the last ones.</summary>
public delegate Task<MediaCredentials> MediaCredentialSource(bool rejected, CancellationToken cancellationToken);

/// <summary>One stream served through an <see cref="IMediaProxy"/>; disposing it ends its requests and refuses new ones.</summary>
public interface IMediaRoute : IDisposable
{
    /// <summary>What the player opens instead of the upstream URL; it carries no credential.</summary>
    Uri LocalUrl { get; }
}

/// <summary>
/// A stream of <see cref="MediaProxy"/>: the player asks the loopback, the route asks the server, with the headers of
/// the moment. A token the server refuses is renewed once and the request repeated, so the player never sees an
/// expiry; a token close to its end is renewed before it is sent.
/// </summary>
internal sealed class MediaRoute : IMediaRoute
{
    // Renewed ahead of its end, as Apple's client does: a request must not start with a token that expires on the way.
    private static readonly TimeSpan RenewAhead = TimeSpan.FromSeconds(60);

    // Request headers a range or segment request needs; everything else stays with the player.
    private static readonly string[] ForwardedRequestHeaders = ["Range", "If-Range", "If-None-Match", "If-Modified-Since", "Accept", "User-Agent"];

    // Response headers the player needs; cookies and anything about the upstream connection stay behind.
    private static readonly HashSet<string> ForwardedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Cache-Control", "Retry-After",
    };

    private readonly MediaProxy _proxy;
    private readonly MediaCredentialSource _source;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _renewing = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly string _origin;
    private readonly string _pathAndQuery;
    private readonly string _allowedPrefix;
    private MediaCredentials? _current;
    private int _disposed;

    public MediaRoute(MediaProxy proxy, string id, string localRoot, Uri upstream, MediaRouteScope scope, MediaCredentialSource source,
        MediaCredentials? initial, TimeProvider time)
    {
        _proxy = proxy;
        Id = id;
        _source = source;
        _current = initial;
        _time = time;
        Upstream = upstream;
        Scope = scope;
        // Plan URLs are replayed byte for byte (docs/api-contract.md, 9.2): the string as given, not as Uri re-escapes it.
        string text = upstream.OriginalString;
        int path = text.IndexOf('/', text.IndexOf("//", StringComparison.Ordinal) + 2);
        _origin = path < 0 ? text : text[..path];
        _pathAndQuery = path < 0 ? "/" : text[path..];
        string pathOnly = _pathAndQuery.Split('?', 2)[0];
        _allowedPrefix = scope == MediaRouteScope.File ? _pathAndQuery : pathOnly[..(pathOnly.LastIndexOf('/') + 1)];
        LocalUrl = new Uri(localRoot + _pathAndQuery);
    }

    public string Id { get; }

    public Uri LocalUrl { get; }

    public Uri Upstream { get; }

    public MediaRouteScope Scope { get; }

    internal CancellationToken Closing => _closing.Token;

    /// <summary>The upstream URL for <paramref name="pathAndQuery"/> as the player asked it, if the route reaches it.</summary>
    internal Uri? Map(string pathAndQuery)
    {
        if (Scope == MediaRouteScope.File)
        {
            return pathAndQuery == _pathAndQuery ? Upstream : null;
        }
        string path = pathAndQuery.Split('?', 2)[0];
        return path.StartsWith(_allowedPrefix, StringComparison.Ordinal) && IsPlainRelative(path[_allowedPrefix.Length..])
            ? new Uri(_origin + pathAndQuery)
            : null;
    }

    internal async Task ForwardAsync(HttpListenerContext context, Uri upstream, HttpMessageInvoker client, ILogger logger)
    {
        var token = _closing.Token;
        bool head = context.Request.HttpMethod == "HEAD";
        MediaCredentials? refused = null;
        while (true)
        {
            MediaCredentials credentials;
            try
            {
                credentials = await CredentialsAsync(refused, token).ConfigureAwait(false);
            }
            catch (MediaAuthorizationException ex)
            {
                logger.LogInformation("No media headers for {Path}: {Failure}", upstream.AbsolutePath, ex.Failure);
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }
            using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, upstream);
            foreach (string name in ForwardedRequestHeaders)
            {
                if (context.Request.Headers[name] is { } value)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
            foreach (var (name, value) in credentials.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && refused is null)
            {
                logger.LogInformation("The server refused the media headers for {Path}; renewing them", upstream.AbsolutePath);
                refused = credentials;
                continue;
            }
            logger.LogDebug("Media {Method} {Path}: {Status}", request.Method, upstream.AbsolutePath, (int)response.StatusCode);
            await RelayAsync(response, context.Response, head, token).ConfigureAwait(false);
            return;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _proxy.Remove(this);
        _closing.Cancel();
        _closing.Dispose();
    }

    // Beside the stream, never above it: no dot segments, no backslashes, nothing encoded that would become one.
    private static bool IsPlainRelative(string rest) =>
        rest.Split('/').All(segment => segment is not ("." or ".."))
        && rest.IndexOf('\\') < 0
        && !rest.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        && !rest.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        && !rest.Contains("%5c", StringComparison.OrdinalIgnoreCase);

    // One renewal for all requests of the route: a request refused with headers already replaced takes the new ones.
    private async Task<MediaCredentials> CredentialsAsync(MediaCredentials? refused, CancellationToken cancellationToken)
    {
        await _renewing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool rejected = refused is not null && ReferenceEquals(refused, _current);
            if (_current is null || rejected || _current.ExpiresAt - _time.GetUtcNow() < RenewAhead)
            {
                _current = await _source(rejected, cancellationToken).ConfigureAwait(false);
            }
            return _current;
        }
        finally
        {
            _renewing.Release();
        }
    }

    private static async Task RelayAsync(HttpResponseMessage upstream, HttpListenerResponse response, bool head, CancellationToken cancellationToken)
    {
        int status = (int)upstream.StatusCode;
        if (status is >= 300 and < 400 && status != (int)HttpStatusCode.NotModified)
        {
            // v2 media routes do not redirect (9.2): one that does is not followed, and its target never sees the headers.
            response.StatusCode = (int)HttpStatusCode.BadGateway;
            return;
        }
        response.StatusCode = status;
        foreach (var (name, values) in upstream.Headers.Concat(upstream.Content.Headers))
        {
            if (!ForwardedResponseHeaders.Contains(name))
            {
                continue;
            }
            string value = string.Join(", ", values);
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                response.ContentType = value;
            }
            else
            {
                response.Headers[name] = value;
            }
        }
        long? length = upstream.Content.Headers.ContentLength;
        if (head || status == (int)HttpStatusCode.NotModified)
        {
            if (length is { } announced)
            {
                response.ContentLength64 = announced;
            }
            return;
        }
        if (length is { } known)
        {
            response.ContentLength64 = known;
        }
        else
        {
            response.SendChunked = true;
        }
        var body = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            try
            {
                await body.CopyToAsync(response.OutputStream, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A body that breaks off must not read as complete: the player has to see a lost connection, not an end.
                response.Abort();
                throw;
            }
        }
    }
}
