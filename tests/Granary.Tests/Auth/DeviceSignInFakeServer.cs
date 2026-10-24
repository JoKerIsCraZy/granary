using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Granary.Tests.Auth;

/// <summary>A request the fake server received, with the fake clock's time since the test started.</summary>
internal sealed record RecordedRequest(string Path, TimeSpan At, string? Body)
{
    public string? Field(string name)
    {
        if (Body is null)
        {
            return null;
        }
        using var document = JsonDocument.Parse(Body);
        return document.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}

/// <summary>One scripted answer; it gets the request's cancellation token.</summary>
internal delegate Task<HttpResponseMessage> Answer(CancellationToken cancellationToken);

/// <summary>
/// The device-login endpoints of a Silo server, scripted per path on top of <see cref="FakeHttpMessageHandler"/>.
/// Each path answers from its own script and repeats the script's last answer. Defaults: capability
/// available with cancel, one start answer, polls pending, cancel accepted.
/// </summary>
internal sealed class DeviceSignInFakeServer
{
    public const string CapabilityPath = "/api/v2/auth/device/capability";
    public const string StartPath = "/api/v2/auth/device/start";
    public const string PollPath = "/api/v2/auth/device/poll";
    public const string CancelPath = "/api/v2/auth/device/cancel";

    public static readonly ServerEntry Server = new("srv1", new Uri("https://silo.example.org/"), "Home", "server-id",
        ManualTimeProvider.DefaultStart, ManualTimeProvider.DefaultStart, null);

    public static readonly ClientIdentity Identity = new("device-id", "TEST-PC", "windows", "Granary", "0.1.0", "dev", "desktop");

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<Answer>> _scripts = [];
    private readonly List<RecordedRequest> _requests = [];

    public DeviceSignInFakeServer(ManualTimeProvider time)
    {
        Time = time;
        Handler = new FakeHttpMessageHandler(RespondAsync);
        Script(CapabilityPath, Json(CapabilityJson()));
        Script(StartPath, StartAnswer("device-1", "4821-7730"));
        Script(PollPath, Pending());
        Script(CancelPath, Json("""{"status":"canceled"}"""));
    }

    public ManualTimeProvider Time { get; }

    public FakeHttpMessageHandler Handler { get; }

    public FakeSessionManager Session { get; } = new();

    public DeviceSignInFlow CreateFlow() =>
        new(new DeviceLoginApi(new SiloHttp(new HttpClient(Handler))), Session, Identity, Time, NullLogger<DeviceSignInFlow>.Instance);

    /// <summary>Replaces the answers of <paramref name="path"/>; the last one repeats.</summary>
    public void Script(string path, params Answer[] answers)
    {
        lock (_gate)
        {
            _scripts[path] = new Queue<Answer>(answers);
        }
    }

    public IReadOnlyList<RecordedRequest> Requests(string path)
    {
        lock (_gate)
        {
            return [.. _requests.Where(r => r.Path == path)];
        }
    }

    public IReadOnlyList<TimeSpan> Times(string path) => [.. Requests(path).Select(r => r.At)];

    public IReadOnlyList<string?> DeviceCodes(string path) => [.. Requests(path).Select(r => r.Field("device_code"))];

    public static string CapabilityJson(string state = "available", bool cancel = true) =>
        $$"""{"state":"{{state}}","cancel":{{(cancel ? "true" : "false")}},"opened_signal":true,"protocol_versions":[2],"revision":"r1"}""";

    /// <summary>A start answer; <paramref name="serverSkew"/> is how far the server clock runs ahead of this PC's.</summary>
    public Answer StartAnswer(string deviceCode, string userCode, long expiresIn = 900, long interval = 3, TimeSpan serverSkew = default) =>
        cancellationToken =>
        {
            string digits = userCode.Replace("-", "", StringComparison.Ordinal);
            var expiresAt = Time.GetUtcNow() + serverSkew + TimeSpan.FromSeconds(expiresIn);
            return Json($$"""
                {"device_code":"{{deviceCode}}","user_code":"{{userCode}}",
                 "verification_uri":"https://silo.example.org/activate",
                 "verification_uri_complete":"https://silo.example.org/activate?code={{digits}}",
                 "expires_at":"{{Iso(expiresAt)}}","expires_in":{{expiresIn}},"interval":{{interval}},
                 "device_name":"TEST-PC","device_platform":"windows","match_code":"apple tree"}
                """, HttpStatusCode.Created)(cancellationToken);
        };

    public static Answer Pending(bool opened = false, long pollAfter = 3, DateTimeOffset? expiresAt = null)
    {
        string expiry = expiresAt is { } at ? ",\"expires_at\":\"" + Iso(at) + "\"" : "";
        return Json($$"""{"status":"pending","poll_after":{{pollAfter}},"opened":{{(opened ? "true" : "false")}}{{expiry}},"profile_id":"","profile_token":"","temporary":false}""");
    }

    public static Answer Status(string status) =>
        Json($$"""{"status":"{{status}}","poll_after":3,"opened":false,"profile_id":"","profile_token":"","temporary":false}""");

    public static Answer Approved(bool withTokens = true, string accessToken = "access-secret") => withTokens
        ? Json("""
            {"status":"approved","poll_after":3,"opened":true,"profile_id":"","profile_token":"","temporary":false,
             "tokens":{"access_token":"ACCESS","refresh_token":"refresh-secret","expires_in":900,
                       "user":{"id":"1","username":"elena","email":"","role":"user","permissions":[],
                               "download_allowed":true,"password_change_required":false}}}
            """.Replace("ACCESS", accessToken, StringComparison.Ordinal))
        : Status("approved");

    public static Answer Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        _ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public static Answer Problem(int status, string code, TimeSpan? retryAfter = null) => _ =>
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(
                $$"""{"type":"{{ApiProblem.TypeOrigin}}{{code}}","title":"{{code}}","status":{{status}}}""",
                Encoding.UTF8, "application/problem+json"),
        };
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }
        return Task.FromResult(response);
    };

    public static Answer NetworkError() =>
        _ => Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"));

    /// <summary>Never answers; ends only when the request is canceled.</summary>
    public static Answer Hang() => async cancellationToken =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    };

    /// <summary>Answers when the test completes <paramref name="answer"/>, ignoring cancellation (a response already on its way).</summary>
    public static Answer Held(TaskCompletionSource<Answer> answer) => async cancellationToken =>
    {
        var next = await answer.Task;
        return await next(CancellationToken.None);
    };

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Answer answer;
        lock (_gate)
        {
            _requests.Add(new RecordedRequest(path, Time.Elapsed, body));
            var script = _scripts[path];
            answer = script.Count > 1 ? script.Dequeue() : script.Peek();
        }
        return await answer(cancellationToken);
    }
}
