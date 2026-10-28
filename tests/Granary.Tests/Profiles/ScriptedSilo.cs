using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Granary.Core.Api;
using Granary.Core.Servers;
using Granary.Tests.Fakes;

namespace Granary.Tests.Profiles;

/// <summary>
/// What the client sent; the body is read at send time because SiloHttp disposes the request.
/// <paramref name="SavedAddressOnly"/>: it may not take a local route.
/// </summary>
internal sealed record SentRequest(HttpMethod Method, Uri Uri, string? Body, SiloAuth Auth, bool SavedAddressOnly = false, string? IfMatch = null);

/// <summary>
/// A Silo server behind a reverse-proxy path prefix, scripted per method and URL. Each route answers
/// with its responders in order and repeats the last one; an unscripted request fails the test.
/// </summary>
internal sealed class ScriptedSilo
{
    public static readonly Uri Base = new("https://nas.example/silo");

    public static readonly ServerEntry Server = Entry("srv1", Base);

    private readonly Dictionary<string, Queue<Func<Task<HttpResponseMessage>>>> _routes = new(StringComparer.Ordinal);

    public ScriptedSilo()
    {
        Handler = new FakeHttpMessageHandler(RespondAsync);
        Http = new SiloHttp(new HttpClient(Handler));
    }

    public FakeHttpMessageHandler Handler { get; }

    public SiloHttp Http { get; }

    public List<SentRequest> Sent { get; } = [];

    public static ServerEntry Entry(string id, Uri baseUri) =>
        new(id, baseUri, "NAS", "server-" + id, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);

    public static Uri Url(string path, Uri? server = null) => SiloHttp.BuildUri(server ?? Base, path);

    public ScriptedSilo On(HttpMethod method, string path, params Func<Task<HttpResponseMessage>>[] responders) =>
        On(method, Url(path), responders);

    public ScriptedSilo On(HttpMethod method, Uri url, params Func<Task<HttpResponseMessage>>[] responders)
    {
        _routes[Key(method, url)] = new Queue<Func<Task<HttpResponseMessage>>>(responders);
        return this;
    }

    public IReadOnlyList<SentRequest> SentTo(HttpMethod method, string path) =>
        [.. Sent.Where(r => r.Method == method && Key(r.Method, r.Uri) == Key(method, Url(path)))];

    public static Func<Task<HttpResponseMessage>> Json(HttpStatusCode status, string json) =>
        () => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public static Func<Task<HttpResponseMessage>> NoContent() =>
        () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

    /// <summary>An RFC 9457 problem; <paramref name="errorsJson"/> is the raw <c>errors</c> array.</summary>
    public static Func<Task<HttpResponseMessage>> Problem(int status, string code, TimeSpan? retryAfter = null, string? errorsJson = null) => () =>
    {
        string errors = errorsJson is null ? "" : $", \"errors\": {errorsJson}";
        string body = $$"""{"type": "{{ApiProblem.TypeOrigin}}{{code}}", "title": "{{code}}", "status": {{status}}, "detail": "Problem detail"{{errors}}}""";
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
        };
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }
        return Task.FromResult(response);
    };

    public static Func<Task<HttpResponseMessage>> NetworkError() =>
        () => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;
        bool savedOnly = request.Options.TryGetValue(SiloRequestOptions.SavedAddressOnly, out bool only) && only;
        string? ifMatch = request.Headers.TryGetValues("If-Match", out var versions) ? string.Join(",", versions) : null;
        Sent.Add(new SentRequest(request.Method, uri, body, request.GetAuth(), savedOnly, ifMatch));
        if (!_routes.TryGetValue(Key(request.Method, uri), out var responders) || responders.Count == 0)
        {
            throw new InvalidOperationException($"Unscripted request {request.Method} {uri}");
        }
        var respond = responders.Count > 1 ? responders.Dequeue() : responders.Peek();
        return await respond();
    }

    private static string Key(HttpMethod method, Uri url) => $"{method} {url.GetLeftPart(UriPartial.Path)}";
}
