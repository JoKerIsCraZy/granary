using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Granary.Tests.Fakes;

/// <summary>What the fake server saw of one request, read while the request was still alive.</summary>
internal sealed record RecordedRequest(HttpMethod Method, string Path, string? Authorization, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);

    public static async Task<RecordedRequest> FromAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        return new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), headers, body);
    }
}

/// <summary>
/// A Silo server double for the authentication pipeline. It issues numbered token pairs and rotates
/// strictly: after a refresh only the newest access and refresh tokens are accepted.
/// </summary>
internal sealed class FakeSiloServer : HttpMessageHandler
{
    private int _generation = 1;
    private int _refreshCalls;

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Answers a request itself when it returns a response; null falls through to the default behaviour.</summary>
    public Func<RecordedRequest, HttpResponseMessage?>? Intercept { get; set; }

    public TimeSpan RefreshDelay { get; set; }

    public bool PasswordChangeRequired { get; set; }

    public string AccessToken => $"access-{Volatile.Read(ref _generation)}";

    public string RefreshToken => $"refresh-{Volatile.Read(ref _generation)}";

    public int RefreshCalls => Volatile.Read(ref _refreshCalls);

    public IEnumerable<RecordedRequest> RequestsTo(string path) => Requests.Where(r => r.Path == path);

    public static HttpResponseMessage Problem(int status, string code, int? retryAfterSeconds = null)
    {
        var response = Json(status, $$"""{"type":"https://siloserver.org/docs/api/v2/problems/{{code}}","title":"{{code}}","status":{{status}}}""",
            "application/problem+json");
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }
        return response;
    }

    public static HttpResponseMessage Json(int status, string json, string mediaType = "application/json") =>
        new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = await RecordedRequest.FromAsync(request, cancellationToken);
        Requests.Enqueue(recorded);
        if (Intercept?.Invoke(recorded) is { } intercepted)
        {
            return intercepted;
        }
        switch (recorded.Path)
        {
            case "/api/v2/auth/login":
                return Json(200, $$"""{"access_token":"{{AccessToken}}","refresh_token":"{{RefreshToken}}","expires_in":28800,"user":{{UserJson()}}}""");
            case "/api/v2/auth/refresh":
                return await RefreshAsync(recorded, cancellationToken);
            case "/api/v2/auth/providers":
                return Json(200, """{"items":[],"password_login":true}""");
        }
        if (recorded.Authorization != $"Bearer {AccessToken}")
        {
            return Problem(401, "invalid_token");
        }
        return recorded.Path switch
        {
            "/api/v2/auth/logout" => new HttpResponseMessage(HttpStatusCode.NoContent),
            "/api/v2/account/me" => Json(200, UserJson()),
            "/api/v2/profiles" => Json(200, """{"items":[{"id":"7","name":"Alex","is_primary":true,"has_pin":false,"is_child":false}],"avatar_upload_enabled":false}"""),
            _ => Json(200, "{}"),
        };
    }

    private async Task<HttpResponseMessage> RefreshAsync(RecordedRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _refreshCalls);
        if (RefreshDelay > TimeSpan.Zero)
        {
            await Task.Delay(RefreshDelay, cancellationToken);
        }
        string? presented = JsonDocument.Parse(request.Body).RootElement.GetProperty("refresh_token").GetString();
        if (presented != RefreshToken)
        {
            return Problem(401, "invalid_token");
        }
        Interlocked.Increment(ref _generation);
        PasswordChangeRequired = false;
        return Json(200, $$"""{"access_token":"{{AccessToken}}","refresh_token":"{{RefreshToken}}","expires_in":28800}""");
    }

    private string UserJson() =>
        $$"""{"id":"1","username":"alex","email":"","role":"user","permissions":[],"download_allowed":true,"password_change_required":{{(PasswordChangeRequired ? "true" : "false")}}}""";
}
