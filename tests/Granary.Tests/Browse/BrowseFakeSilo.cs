using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Tests.Catalog;
using Granary.Tests.Fakes;

namespace Granary.Tests.Browse;

/// <summary>What a browse view model sent; the body is read at send time because SiloHttp disposes the request.</summary>
internal sealed record BrowseRequest(HttpMethod Method, Uri Uri, string? Body)
{
    public string? Query(string name) => HttpUtility.ParseQueryString(Uri.Query)[name];
}

/// <summary>
/// A Silo server for the Home, "Explore all" and Search view models: each path answers through a scripted
/// function (query and method are the function's business), every request is recorded, and an unscripted
/// path fails the test. Image capabilities are the contract fixture, so artwork widths are sent.
/// </summary>
internal sealed class BrowseFakeSilo
{
    public static readonly Uri Base = new("https://silo.test/media");

    public static readonly ServerEntry Server = new("srv1", Base, "Test server", null, default, default, null);

    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _routes =
        new(StringComparer.Ordinal);

    public BrowseFakeSilo()
    {
        Http = new SiloHttp(new HttpClient(new FakeHttpMessageHandler(RespondAsync)));
        Catalog = new CatalogApi(Http);
        Images = new ImageParameterCache(new ImagesApi(Http), Logs.For<ImageParameterCache>());
        Map("api/v2/images/capabilities", CatalogPayloads.ImageCapabilities);
    }

    public SiloHttp Http { get; }

    public CatalogApi Catalog { get; }

    public ImageParameterCache Images { get; }

    public LogSink Logs { get; } = new();

    public ConcurrentQueue<BrowseRequest> Requests { get; } = new();

    /// <summary>A session with a profile acting on <see cref="Server"/>.</summary>
    public static FakeSessionManager ReadySession(string profileId = "7") =>
        new() { State = SessionState.Ready, ActiveServer = Server, ProfileId = profileId };

    public BrowseFakeSilo Map(string path, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _routes[path] = respond;
        return this;
    }

    public BrowseFakeSilo Map(string path, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        Map(path, (request, _) => Task.FromResult(respond(request)));

    public BrowseFakeSilo Map(string path, string json) => Map(path, _ => Json(json));

    /// <summary>The requests to <paramref name="path"/> (relative to the server base, without query).</summary>
    public IReadOnlyList<BrowseRequest> To(string path) =>
        [.. Requests.Where(r => r.Uri.AbsolutePath == Base.AbsolutePath.TrimEnd('/') + "/" + path)];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

    public static HttpResponseMessage Problem(int status, string code, TimeSpan? retryAfter = null)
    {
        string body = $$"""{"type": "{{ApiProblem.TypeOrigin}}{{code}}", "title": "{{code}}", "status": {{status}}, "detail": "Silo internal detail"}""";
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
        };
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }
        return response;
    }

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;
        Requests.Enqueue(new BrowseRequest(request.Method, uri, body));
        string path = uri.AbsolutePath[(Base.AbsolutePath.TrimEnd('/').Length + 1)..];
        if (!_routes.TryGetValue(Uri.UnescapeDataString(path), out var respond))
        {
            throw new InvalidOperationException($"Unscripted request {request.Method} {uri.AbsolutePath}");
        }
        return await respond(request, cancellationToken);
    }
}
