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

    /// <summary>As <see cref="GetAsync{TResponse}"/>, but always through the saved address, never a local route.</summary>
    public async Task<TResponse> GetFromSavedAddressAsync<TResponse>(Uri server, string path, SiloAuth auth, JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        using var request = Create(HttpMethod.Get, server, path, auth);
        request.Options.Set(SiloRequestOptions.SavedAddressOnly, true);
        return await SendAsync(request, responseType, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(Uri server, string path, SiloAuth auth, TRequest body, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        using var request = Create(HttpMethod.Post, server, path, auth);
        request.Content = JsonContent.Create(body, requestType);
        return await SendAsync(request, responseType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>For operations that take a form (<c>multipart/form-data</c>), as file uploads do.</summary>
    public Task<TResponse> PostFormAsync<TResponse>(Uri server, string path, SiloAuth auth, MultipartFormDataContent form,
        JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken) =>
        SendFormAsync(HttpMethod.Post, server, path, auth, form, responseType, cancellationToken);

    /// <summary>As <see cref="PostFormAsync{TResponse}"/>, for a form sent with another method: a poster is a <c>PUT</c>.</summary>
    public async Task<TResponse> SendFormAsync<TResponse>(HttpMethod method, Uri server, string path, SiloAuth auth, MultipartFormDataContent form,
        JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        using var request = Create(method, server, path, auth);
        request.Content = form;
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

    /// <summary>For bodiless operations of any method that answer JSON: putting a title on the watchlist is a <c>PUT</c>.</summary>
    public async Task<TResponse> SendAsync<TResponse>(HttpMethod method, Uri server, string path, SiloAuth auth, JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        using var request = Create(method, server, path, auth);
        return await SendAsync(request, responseType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// For operations that take a JSON body and answer JSON whatever their method: stopping playback is a
    /// <c>DELETE</c> with both (docs/api-contract.md, 10.3).
    /// </summary>
    public async Task<TResponse> SendAsync<TRequest, TResponse>(HttpMethod method, Uri server, string path, SiloAuth auth, TRequest body,
        JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        using var request = Create(method, server, path, auth);
        request.Content = JsonContent.Create(body, requestType);
        return await SendAsync(request, responseType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// For a bodiless operation of any method that answers JSON and needs headers of its own, as a Watch Party room's
    /// proof (<c>X-Room-Token</c>). They go with this request only and are never logged.
    /// </summary>
    public async Task<TResponse> SendWithHeadersAsync<TResponse>(HttpMethod method, Uri server, string path, SiloAuth auth,
        IReadOnlyDictionary<string, string> headers, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headers);
        using var request = Create(method, server, path, auth);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        return await SendAsync(request, responseType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>As <see cref="GetAsync{TResponse}"/>, with the answer's version (its ETag) for a later write's <c>If-Match</c>.</summary>
    public async Task<Versioned<TResponse>> GetVersionedAsync<TResponse>(Uri server, string path, SiloAuth auth, JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        using var request = Create(HttpMethod.Get, server, path, auth);
        return await SendVersionedAsync(request, responseType, requireVersion: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A write the server takes only at the version it was read at (<c>If-Match</c>); one changed since answers 412.
    /// Answers the result and its new version, empty when the answer names none (the write still went through).
    /// </summary>
    public async Task<Versioned<TResponse>> SendIfMatchAsync<TRequest, TResponse>(HttpMethod method, Uri server, string path, SiloAuth auth, string version,
        TRequest body, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        using var request = Create(method, server, path, auth);
        IfMatch(request, version);
        request.Content = JsonContent.Create(body, requestType);
        return await SendVersionedAsync(request, responseType, requireVersion: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>As the other <c>SendIfMatchAsync</c>, for a bodiless write that answers 204, as a delete.</summary>
    public async Task SendIfMatchAsync(HttpMethod method, Uri server, string path, SiloAuth auth, string version, CancellationToken cancellationToken)
    {
        using var request = Create(method, server, path, auth);
        IfMatch(request, version);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// For a download read as it arrives: the answer once its headers are in, whatever its status, for the caller to
    /// read and dispose. <paramref name="url"/> is absolute; credentials still go only beneath <paramref name="server"/>.
    /// </summary>
    public async Task<HttpResponseMessage> OpenAsync(Uri server, Uri url, SiloAuth auth, string accept, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.SetAuth(auth);
        request.SetServer(server);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
        return await ReadAsync(response, responseType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Versioned<TResponse>> SendVersionedAsync<TResponse>(HttpRequestMessage request, JsonTypeInfo<TResponse> responseType,
        bool requireVersion, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        // Without its version nothing read here can be written back; a write that names none still went through.
        string version = response.Headers.ETag?.ToString()
            ?? (requireVersion ? throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server named no version of what it answered.") : "");
        return new Versioned<TResponse>(await ReadAsync(response, responseType, cancellationToken).ConfigureAwait(false), version);
    }

    // The version goes back as the server wrote it.
    private static void IfMatch(HttpRequestMessage request, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        request.Headers.TryAddWithoutValidation("If-Match", version);
    }

    private static async Task<TResponse> ReadAsync<TResponse>(HttpResponseMessage response, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await JsonSerializer.DeserializeAsync(stream, responseType, cancellationToken).ConfigureAwait(false)
                ?? throw new SiloApiException((int)response.StatusCode, ApiProblem.FromStatus((int)response.StatusCode));
        }
        catch (JsonException ex)
        {
            // A captive portal or proxy page, not a Silo answer: to callers that is an unreachable server.
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server did not answer like a Silo server.", ex);
        }
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

    /// <summary>
    /// An id as one escaped path segment. "." and ".." stay unescaped and would be removed as dot segments,
    /// addressing another route.
    /// </summary>
    /// <exception cref="ArgumentException">The value is blank, "." or "..".</exception>
    internal static string PathSegment(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (value is "." or "..")
        {
            throw new ArgumentException($"'{value}' is not an id.", paramName);
        }
        return Escape(value);
    }

    internal static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What a read answered, with its version (the <c>ETag</c>) that a later write sends back as <c>If-Match</c>.</summary>
public sealed record Versioned<T>(T Value, string Version);
