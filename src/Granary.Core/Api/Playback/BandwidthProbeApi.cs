using System.Net;
using System.Net.Http.Headers;

namespace Granary.Core.Api.Playback;

/// <summary>
/// The connection speed test's calls (<c>connection_speed_v1</c>; the server's buffering-quality design, Connection
/// speed test): the probe download, timed by the caller, and the report of what it measured.
/// </summary>
public sealed class BandwidthProbeApi(SiloHttp http, IHttpClientFactory clients)
{
    /// <summary>
    /// The client of a redirect's one hop, to a short-lived proxy address that carries its own token: no credentials, no
    /// identity headers, and no log of the address.
    /// </summary>
    public const string HopClientName = "Granary.BandwidthProbeHop";

    private const string OctetStream = "application/octet-stream";

    // A 429 without a usable Retry-After holds tests this long.
    private const int DefaultRetryAfterSeconds = 30;

    /// <summary>
    /// Asks <paramref name="url"/> (the advertised <c>bandwidth_probe_url</c> with its <c>size</c>) for the probe's bytes,
    /// with this app's credentials only beneath <paramref name="server"/>. A redirect is followed once, with none of
    /// them. The answer's body is the caller's to read; disposing the answer ends the transfer.
    /// </summary>
    public async Task<BandwidthProbeAnswer> OpenAsync(Uri server, Uri url, CancellationToken cancellationToken)
    {
        var response = await http.OpenAsync(server, url, SiloAuth.Profile, OctetStream, cancellationToken).ConfigureAwait(false);
        if (!IsRedirect(response.StatusCode))
        {
            return Answer(response);
        }
        int status = (int)response.StatusCode;
        var location = response.Headers.Location;
        var from = response.RequestMessage?.RequestUri ?? url;
        response.Dispose();
        var target = location is null ? null : location.IsAbsoluteUri ? location : new Uri(from, location);
        if (target is null || target.Scheme is not ("https" or "http"))
        {
            return BandwidthProbeAnswer.Unavailable(status);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(OctetStream));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        var hop = await clients.CreateClient(HopClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // A second redirect is not followed.
        return Answer(hop);
    }

    /// <summary>
    /// Keeps a result for this device on the connection it uses now (204). <c>manual</c> marks <b>Test speed</b>. The
    /// server keys it by the connection it arrives over, so it goes the way the probe and the connection's reads go:
    /// over a verified local route. Sent twice, the server keeps the latest.
    /// </summary>
    public Task ReportAsync(Uri server, BandwidthProbeReport report, CancellationToken cancellationToken = default) =>
        http.PostRepeatableAsync(server, "api/v2/playback/bandwidth-probe/results", SiloAuth.Profile, report,
            PlaybackJsonContext.Default.BandwidthProbeReport, cancellationToken);

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    private static BandwidthProbeAnswer Answer(HttpResponseMessage response)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return BandwidthProbeAnswer.Body(response);
            case HttpStatusCode.TooManyRequests:
                int seconds = response.Headers.RetryAfter?.Delta is { } delta
                    ? (int)Math.Clamp(Math.Ceiling(delta.TotalSeconds), 1, int.MaxValue)
                    : DefaultRetryAfterSeconds;
                response.Dispose();
                return BandwidthProbeAnswer.RateLimited(seconds);
            default:
                int status = (int)response.StatusCode;
                response.Dispose();
                return BandwidthProbeAnswer.Unavailable(status);
        }
    }
}

public enum BandwidthProbeAnswerKind
{
    /// <summary>200: the bytes follow.</summary>
    Body,

    /// <summary>429: the server allows another probe after <see cref="BandwidthProbeAnswer.RetryAfterSeconds"/>.</summary>
    RateLimited,

    /// <summary>Any other answer, for one a proxy-only policy with no proxy that serves probes. Nothing else is measured.</summary>
    Unavailable,
}

/// <summary>How a probe download answered; disposing it closes the transfer, and the server stops at its next write.</summary>
public sealed class BandwidthProbeAnswer : IDisposable
{
    private readonly HttpResponseMessage? _response;

    private BandwidthProbeAnswer(BandwidthProbeAnswerKind kind, HttpResponseMessage? response, int retryAfterSeconds, int status)
    {
        Kind = kind;
        _response = response;
        RetryAfterSeconds = retryAfterSeconds;
        Status = status;
    }

    public BandwidthProbeAnswerKind Kind { get; }

    public int RetryAfterSeconds { get; }

    /// <summary>The HTTP status of an unavailable probe.</summary>
    public int Status { get; }

    internal static BandwidthProbeAnswer Body(HttpResponseMessage response) => new(BandwidthProbeAnswerKind.Body, response, 0, 200);

    internal static BandwidthProbeAnswer RateLimited(int seconds) => new(BandwidthProbeAnswerKind.RateLimited, null, seconds, 429);

    internal static BandwidthProbeAnswer Unavailable(int status) => new(BandwidthProbeAnswerKind.Unavailable, null, 0, status);

    /// <summary>The bytes as they arrive; only for <see cref="BandwidthProbeAnswerKind.Body"/>.</summary>
    public Task<Stream> ReadBodyAsync(CancellationToken cancellationToken) =>
        _response?.Content.ReadAsStreamAsync(cancellationToken) ?? throw new InvalidOperationException("The probe sent no body.");

    public void Dispose() => _response?.Dispose();
}

/// <summary>
/// <c>POST /api/v2/playback/bandwidth-probe/results</c>: the bytes and time the rate came from, and the transport it was
/// taken on (<c>wifi</c>, <c>ethernet</c>, <c>cellular</c> or <c>other</c>).
/// </summary>
public sealed record BandwidthProbeReport
{
    public required long Kbps { get; init; }

    public bool LowerBound { get; init; }

    public required long Bytes { get; init; }

    public required long DurationMs { get; init; }

    public required string Transport { get; init; }

    /// <summary>The viewer asked for it (<b>Test speed</b>): the result replaces the playback sample, and lifts a learned limit it beats.</summary>
    public bool? Manual { get; init; }
}
