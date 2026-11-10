using System.Buffers;
using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
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
/// expiry; a token close to its end is renewed before it is sent. A direct play body the server breaks off goes on from
/// its next byte (<see cref="UpstreamResume"/>), so the player never sees that either.
/// </summary>
internal sealed class MediaRoute : IMediaRoute
{
    // Renewed ahead of its end, as Apple's client does: a request must not start with a token that expires on the way.
    private static readonly TimeSpan RenewAhead = TimeSpan.FromSeconds(60);

    // A renewal ahead of the end that brought no new token is not tried again for this long: the server may be
    // unable to refresh for now, and every segment request would otherwise wait for another failing refresh.
    private static readonly TimeSpan RenewalPause = TimeSpan.FromSeconds(5);

    // mpv opens a few connections at a time (a seek, the next segments); more than this is not a player.
    private const int MaxConcurrentRequests = 16;

    // As Stream.CopyToAsync reads.
    private const int CopyBufferSize = 81920;

    /// <summary>
    /// How long a request waits for the server's next bytes (the answer, or more of its body) before it is cut off. The
    /// server may hold a playlist 30 s and a segment about 12 s, up to 30 s more after a seek restarts the transcode
    /// (docs/api-contract.md, 9.5: client timeouts above about 45 s); a minute clears that with room to spare. Longer
    /// is a server gone silent, such as a connection it closed during a pause whose end never arrived: mpv's HTTP reader
    /// would wait for it for good.
    /// </summary>
    internal static readonly TimeSpan UpstreamIdleTimeout = TimeSpan.FromSeconds(60);

    // Sent exactly as written: no unescaping (%41 stays %41) and no resolving of dot segments, which IsPlainRelative
    // has already refused. Plan URLs are replayed byte for byte (docs/api-contract.md, 9.2).
    private static readonly UriCreationOptions Verbatim = new() { DangerousDisablePathAndQueryCanonicalization = true };

    // Request headers a range or segment request needs; everything else stays with the player.
    private static readonly string[] ForwardedRequestHeaders = ["Range", "If-Range", "If-None-Match", "If-Modified-Since", "Accept", "User-Agent"];

    // A resume asks its own range under its own condition (UpstreamResume); the player's conditions could answer a 304 or
    // a 412 in the middle of a body.
    private static readonly string[] ResumedRequestHeaders = ["Accept", "User-Agent"];

    // Response headers the player needs; cookies and anything about the upstream connection stay behind.
    // Content-Encoding too: the proxy does not decompress, so mpv must know what it gets.
    private static readonly HashSet<string> ForwardedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Encoding", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Cache-Control", "Retry-After",
    };

    private readonly MediaProxy _proxy;
    private readonly MediaCredentialSource _source;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _renewing = new(1, 1);
    private readonly SemaphoreSlim _requests = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly CancellationTokenSource _closing = new();
    private readonly string _origin;
    private readonly string _pathAndQuery;
    private readonly string _allowedPrefix;
    private MediaCredentials? _current;
    private long? _renewedAt;
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
        LocalUrl = new Uri(localRoot + _pathAndQuery, Verbatim);
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
            return pathAndQuery == _pathAndQuery ? new Uri(_origin + _pathAndQuery, Verbatim) : null;
        }
        string path = pathAndQuery.Split('?', 2)[0];
        return path.StartsWith(_allowedPrefix, StringComparison.Ordinal) && IsPlainRelative(path[_allowedPrefix.Length..])
            ? new Uri(_origin + pathAndQuery, Verbatim)
            : null;
    }

    internal async Task ForwardAsync(HttpListenerContext context, Uri upstream, HttpMessageInvoker client, ILogger logger)
    {
        var token = _closing.Token;
        if (!await _requests.WaitAsync(0, token).ConfigureAwait(false))
        {
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            return;
        }
        try
        {
            await ForwardOnceAsync(context, upstream, client, logger, token).ConfigureAwait(false);
        }
        finally
        {
            _requests.Release();
        }
    }

    private async Task ForwardOnceAsync(HttpListenerContext context, Uri upstream, HttpMessageInvoker client, ILogger logger, CancellationToken token)
    {
        bool head = context.Request.HttpMethod == "HEAD";
        var player = context.Request.Headers;
        using var watch = new UpstreamWatch(UpstreamIdleTimeout, _time, token);
        HttpResponseMessage answer;
        try
        {
            answer = await SendAsync(() => Request(head ? HttpMethod.Head : HttpMethod.Get, upstream, player, ForwardedRequestHeaders),
                client, logger, watch).ConfigureAwait(false);
        }
        catch (MediaAuthorizationException ex)
        {
            // No token for now is a busy server, not a refusal; mpv then reports a lost connection, not a 401.
            logger.LogInformation("No media headers for {Path}: {Failure}", upstream.AbsolutePath, ex.Failure);
            context.Response.StatusCode = ex.Failure == MediaAuthorizationFailure.Unavailable
                ? (int)HttpStatusCode.ServiceUnavailable
                : (int)HttpStatusCode.Unauthorized;
            return;
        }
        using var response = answer;
        logger.LogDebug("Media {Method} {Path}: {Status}", context.Request.HttpMethod, upstream.AbsolutePath, (int)response.StatusCode);
        // A direct play body the server breaks off goes on from its next byte, asked as the first was.
        using var resume = head ? null : UpstreamResume.For(response, Resend, _time, logger);
        await RelayAsync(response, context.Response, head, watch, resume, token).ConfigureAwait(false);

        Task<HttpResponseMessage> Resend(RangeHeaderValue range, RangeConditionHeaderValue? condition) => SendAsync(() =>
        {
            var request = Request(HttpMethod.Get, upstream, player, ResumedRequestHeaders);
            request.Headers.Range = range;
            request.Headers.IfRange = condition;
            return request;
        }, client, logger, watch);
    }

    /// <summary>
    /// One request to the server with the headers of the moment, timed whole by the watch: the headers (a token refresh
    /// among them), then the answer. A refusal renews the headers once and repeats the request, so the player never sees
    /// an expiry. The answer is the caller's; the request, a GET or HEAD without content, holds nothing to free.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, HttpMessageInvoker client, ILogger logger, UpstreamWatch watch)
    {
        MediaCredentials? refused = null;
        while (true)
        {
            var request = build();
            MediaCredentials credentials;
            HttpResponseMessage answer;
            var waiting = watch.Arm();
            try
            {
                credentials = await CredentialsAsync(refused, waiting).ConfigureAwait(false);
                foreach (var (name, value) in credentials.Headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
                answer = await client.SendAsync(request, waiting).ConfigureAwait(false);
            }
            catch (Exception ex) when (watch.IsCutOff)
            {
                throw watch.CutOff(ex);
            }
            finally
            {
                watch.Disarm();
            }
            if (answer.StatusCode == HttpStatusCode.Unauthorized && refused is null)
            {
                logger.LogInformation("The server refused the media headers for {Path}; renewing them", request.RequestUri!.AbsolutePath);
                answer.Dispose();
                refused = credentials;
                continue;
            }
            return answer;
        }
    }

    // A request upstream with those of the player's headers that are named.
    private static HttpRequestMessage Request(HttpMethod method, Uri upstream, NameValueCollection player, string[] names)
    {
        var request = new HttpRequestMessage(method, upstream);
        foreach (string name in names)
        {
            if (player[name] is { } value)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return request;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _proxy.Remove(this);
        // Cancelled, not disposed: a request between lookup and forwarding still reads the token.
        _closing.Cancel();
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
            bool ending = _current is not null && _current.ExpiresAt - _time.GetUtcNow() < RenewAhead
                && (_renewedAt is not { } renewed || _time.GetElapsedTime(renewed) >= RenewalPause);
            if (_current is null || rejected || ending)
            {
                _renewedAt = _time.GetTimestamp();
                // Bounded by the token also where the source does not watch it.
                _current = await _source(rejected, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return _current;
        }
        finally
        {
            _renewing.Release();
        }
    }

    private static async Task RelayAsync(HttpResponseMessage upstream, HttpListenerResponse response, bool head, UpstreamWatch watch,
        UpstreamResume? resume, CancellationToken cancellationToken)
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
        if (head || status is (int)HttpStatusCode.NotModified or (int)HttpStatusCode.NoContent or < 200)
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
                await CopyAsync(body, response.OutputStream, watch, cancellationToken, resume).ConfigureAwait(false);
            }
            catch
            {
                // A body that breaks off and cannot be resumed must not read as complete: the player has to see a lost
                // connection, not an end. curl then asks again from where it was, if the stream can seek; a remux ends,
                // and the playback reconnects.
                response.Abort();
                throw;
            }
        }
    }

    /// <summary>
    /// Copies the server's <paramref name="body"/> to the <paramref name="player"/>. Only the reads wait on the server and
    /// are timed; a write waits as long as the player does not read, which a paused player does not. With a
    /// <paramref name="resume"/>, a body that breaks off, goes silent or ends early goes on from its next byte.
    /// </summary>
    internal static async Task CopyAsync(Stream body, Stream player, UpstreamWatch watch, CancellationToken cancellationToken,
        UpstreamResume? resume = null)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                int read;
                MediaUpstreamException? failure = null;
                var waiting = watch.Arm();
                try
                {
                    read = await body.ReadAsync(buffer, waiting).ConfigureAwait(false);
                }
                catch (Exception ex) when (watch.IsCutOff)
                {
                    (read, failure) = (0, watch.CutOff(ex));
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
                {
                    (read, failure) = (0, new MediaUpstreamException($"broke off ({Kind(ex)})", ex));
                }
                finally
                {
                    watch.Disarm();
                }
                if (read > 0)
                {
                    resume?.Delivered(read);
                    await player.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (resume is null && failure is not null)
                {
                    throw failure;
                }
                if (resume is not { Remains: true })
                {
                    // The end; with every byte at the player, a break after the last one is the end too.
                    return;
                }
                // A resumed answer may also end before its range does, as servers cap their answers: asked again from there.
                failure ??= new MediaUpstreamException("ended early");
                body = await resume.ResumeAsync(failure, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // What went wrong, never the message: some carry the address.
    internal static string Kind(Exception ex) => ex switch
    {
        HttpIOException io => io.HttpRequestError.ToString(),
        HttpRequestException request => request.HttpRequestError.ToString(),
        _ => ex.GetType().Name,
    };
}
