using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Granary.Core.Api;

/// <summary>
/// The one way Granary sends Silo API requests: builds the URL under the server base (keeping a
/// reverse-proxy path prefix), serializes with <see cref="SiloJsonContext"/>, and turns every
/// non-success answer into a <see cref="SiloApiException"/> with the parsed Problem Details.
/// </summary>
public sealed class SiloHttp(HttpClient http)
{
    public async Task<TResponse> GetAsync<TResponse>(Uri server, string path, SiloAuth auth, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        using var request = Create(HttpMethod.Get, server, path, auth);
        return await SendAsync(request, responseType, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(Uri server, string path, SiloAuth auth, TRequest body, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        using var request = Create(HttpMethod.Post, server, path, auth);
        request.Content = JsonContent.Create(body, requestType);
        return await SendAsync(request, responseType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>For operations that answer 204 No Content.</summary>
    public async Task SendAsync<TRequest>(HttpMethod method, Uri server, string path, SiloAuth auth, TRequest body, JsonTypeInfo<TRequest> requestType, CancellationToken cancellationToken)
    {
        using var request = Create(method, server, path, auth);
        request.Content = JsonContent.Create(body, requestType);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>For bodiless operations that answer 204 No Content.</summary>
    public async Task SendAsync(HttpMethod method, Uri server, string path, SiloAuth auth, CancellationToken cancellationToken)
    {
        using var request = Create(method, server, path, auth);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public static Uri BuildUri(Uri server, string path) => new(server.AbsoluteUri.TrimEnd('/') + "/" + path.TrimStart('/'));

    private static HttpRequestMessage Create(HttpMethod method, Uri server, string path, SiloAuth auth)
    {
        var request = new HttpRequestMessage(method, BuildUri(server, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.SetAuth(auth);
        request.SetServer(server);
        return request;
    }

    private async Task<TResponse> SendAsync<TResponse>(HttpRequestMessage request, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, responseType, cancellationToken).ConfigureAwait(false)
            ?? throw new SiloApiException((int)response.StatusCode, ApiProblem.FromStatus((int)response.StatusCode));
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        int status = (int)response.StatusCode;
        var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false) ?? ApiProblem.FromStatus(status);
        throw new SiloApiException(status, problem, RetryAfter(response));
    }

    internal static async Task<ApiProblem?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not ("application/problem+json" or "application/json"))
        {
            return null;
        }
        try
        {
            // Bytes, not the content stream: HttpContent hands out one cached stream, and the
            // authentication handler reads the problem before the caller does.
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(body, SiloJsonContext.Default.ApiProblem);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    internal static string Escape(string segment) => Uri.EscapeDataString(segment);

    internal static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
