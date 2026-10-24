using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Discovery;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Servers;

/// <summary>Outcome of probing one candidate URL.</summary>
public abstract record ServerProbeResult(Uri BaseUri)
{
    /// <summary>The server speaks the native v2 API.</summary>
    public sealed record Compatible(Uri BaseUri, SystemInfo Info) : ServerProbeResult(BaseUri);

    /// <summary>The server only has the retired /api/v1 surface; the user must update it.</summary>
    public sealed record UpdateRequired(Uri BaseUri) : ServerProbeResult(BaseUri);

    /// <summary>No decision (network error, timeout, unexpected answer); try the next candidate.</summary>
    public sealed record NoVerdict(Uri BaseUri, string Reason) : ServerProbeResult(BaseUri);
}

public interface IServerProbe
{
    Task<ServerProbeResult> ProbeAsync(Uri baseUri, CancellationToken cancellationToken = default);
}

/// <summary>
/// Probes <c>GET /api/v2/system/info</c> without credentials (docs/api-contract.md, 3.2). Only the
/// exact body Go's <c>http.NotFound</c> writes, with or without its trailing newline, means "v1 only".
/// </summary>
public sealed class ServerProbe : IServerProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private const string GoNotFoundBody = "404 page not found";

    private readonly HttpClient _http;
    private readonly ILogger<ServerProbe> _logger;
    private readonly TimeSpan _timeout;

    public ServerProbe(HttpClient http, ILogger<ServerProbe> logger)
        : this(http, logger, DefaultTimeout)
    {
    }

    internal ServerProbe(HttpClient http, ILogger<ServerProbe> logger, TimeSpan timeout)
    {
        _http = http;
        _logger = logger;
        _timeout = timeout;
    }

    public async Task<ServerProbeResult> ProbeAsync(Uri baseUri, CancellationToken cancellationToken = default)
    {
        var url = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/api/v2/system/info");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Interpret(baseUri, response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServerProbeResult.NoVerdict(baseUri, "timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation("Probe of {Origin} failed: {Error}", baseUri.GetLeftPart(UriPartial.Authority), ex.HttpRequestError);
            return new ServerProbeResult.NoVerdict(baseUri, $"network: {ex.HttpRequestError}");
        }
    }

    private static ServerProbeResult Interpret(Uri baseUri, HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.NotFound)
        {
            return body is GoNotFoundBody or GoNotFoundBody + "\n"
                ? new ServerProbeResult.UpdateRequired(baseUri)
                : new ServerProbeResult.NoVerdict(baseUri, "HTTP 404");
        }
        if (status != HttpStatusCode.OK)
        {
            return new ServerProbeResult.NoVerdict(baseUri, $"HTTP {(int)status}");
        }
        try
        {
            var info = JsonSerializer.Deserialize(body, SiloJsonContext.Default.SystemInfo);
            return info?.ApiMajor == 2
                ? new ServerProbeResult.Compatible(baseUri, info)
                : new ServerProbeResult.NoVerdict(baseUri, $"api_major {info?.ApiMajor}");
        }
        catch (JsonException)
        {
            return new ServerProbeResult.NoVerdict(baseUri, "not a Silo system/info document");
        }
    }
}
