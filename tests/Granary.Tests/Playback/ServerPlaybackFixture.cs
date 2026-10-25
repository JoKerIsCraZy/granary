using System.Net;
using System.Text.Json;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Servers;
using Granary.Tests.Auth;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>
/// A <see cref="ServerPlayback"/> on a <see cref="ScriptedSilo"/> with a fake engine, fake media headers and a manual
/// clock. By default the server knows the movie <see cref="MovieId"/> with a resume point, offers protocol 3 and
/// answers the start with a direct-play plan; tests replace what they need.
/// </summary>
internal sealed class ServerPlaybackFixture
{
    public const string MovieId = "movie-heat-1995";
    public const string EpisodeId = "episode-tvdb-296762-1-5";
    public const string Session = PlaybackPayloads.SessionId;
    public const string OtherSession = "22222222-2222-4222-8222-222222222222";
    public const string StartPath = "api/v2/playback/start";
    public const string ProgressPath = "api/v2/playback/" + Session + "/progress";
    public const string StopPath = "api/v2/playback/" + Session;
    public const string ReplanPath = "api/v2/playback/" + Session + "/replan";
    public const string OtherReplanPath = "api/v2/playback/" + OtherSession + "/replan";
    public const string OtherStopPath = "api/v2/playback/" + OtherSession;
    public const string RouteEventsPath = "api/v2/playback/route-events";

    public ServerPlaybackFixture()
    {
        Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(PlaybackPayloads.WatchStateOk));
        Silo.On(HttpMethod.Get, "api/v2/settings/values/effective", Ok("""{ "items": [], "revision": 1 }"""));
        Silo.On(HttpMethod.Get, "api/v2/playback/capabilities", Ok(PlaybackPayloads.CapabilityAvailable));
        Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds));
        Silo.On(HttpMethod.Post, ProgressPath, Ok("""{ "outcome": "applied" }"""));
        Silo.On(HttpMethod.Delete, StopPath, Ok(PlaybackPayloads.StopCompleted));
        Silo.On(HttpMethod.Post, RouteEventsPath, ScriptedSilo.Json(HttpStatusCode.Accepted, PlaybackPayloads.RouteEventAccepted));
    }

    public ScriptedSilo Silo { get; } = new();

    public FakePlaybackEngine Engine { get; } = new();

    public FakeMediaAuthorization Auth { get; } = new();

    public FakeMediaProxy Proxy { get; } = new();

    public ManualTimeProvider Time { get; } = new();

    public LogSink Logs { get; } = new();

    public FakeSessionManager SessionManager { get; } = new()
    {
        State = SessionState.Ready,
        ActiveServer = ScriptedSilo.Server,
        ProfileId = "7",
    };

    public List<ServerPlaybackStatus> Statuses { get; } = [];

    public static Uri StreamUrl(string session = Session) => new(ScriptedSilo.Base.AbsoluteUri + "/api/v2/stream/" + session);

    public static Func<Task<HttpResponseMessage>> Ok(string json) => ScriptedSilo.Json(HttpStatusCode.OK, json);

    public static Func<Task<HttpResponseMessage>> Created(string json) => ScriptedSilo.Json(HttpStatusCode.Created, json);

    /// <summary>The direct-play decision with another session id, as a new start after the first session ended.</summary>
    public static string StartedAgain => PlaybackPayloads.StartOpaqueIds.Replace(Session, OtherSession, StringComparison.Ordinal);

    public ServerPlayback Create()
    {
        var starter = new PlaybackStarter(
            new WatchApi(Silo.Http),
            new PlaybackApi(Silo.Http),
            new PlaybackPreferencesReader(new SettingsApi(Silo.Http), Logs.For<PlaybackPreferencesReader>()),
            new FakeDeviceProbe(),
            AuthPipeline.Identity,
            SessionManager);
        var playback = new ServerPlayback(
            starter,
            new PlaybackApi(Silo.Http),
            new TrackPreferencesApi(Silo.Http),
            new SettingsApi(Silo.Http),
            new MarkersApi(Silo.Http),
            Auth,
            Proxy,
            Engine,
            new ImmediateDispatcher(),
            Time,
            Logs.AsFactory(),
            new ServerPlaybackOptions(new PlaybackRetryPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5))));
        playback.StatusChanged += (_, status) => Statuses.Add(status);
        return playback;
    }

    public async Task<ServerPlayback> StartAsync(PlayRequest? request = null)
    {
        var playback = Create();
        await playback.StartAsync(request ?? PlayRequest.Resume(MovieId), TestContext.Current.CancellationToken);
        return playback;
    }

    /// <summary>Starts the movie and lets the engine show its first frame at the plan's start (12.5 s).</summary>
    public async Task<ServerPlayback> PlayingAsync()
    {
        var playback = await StartAsync();
        Engine.RaiseState(Frame(12.5));
        return playback;
    }

    public static PlaybackSnapshot Frame(double playerSeconds, bool paused = false) =>
        new(true, paused, TimeSpan.FromSeconds(playerSeconds), TimeSpan.FromSeconds(10200), "d3d11va", HasStarted: true);

    /// <summary>The proxy route a load opened: what it serves, and how.</summary>
    public FakeMediaRoute RouteOf(MediaSource load) => Proxy.Routes.Single(r => r.LocalUrl == load.Url);

    /// <summary>The server URL the latest load plays through the proxy.</summary>
    public Uri LastUpstream => RouteOf(Engine.Loaded[^1]).Upstream;

    public JsonElement Body(HttpMethod method, string path, int index = 0) =>
        JsonDocument.Parse(Silo.SentTo(method, path)[index].Body!).RootElement;

    public IReadOnlyList<string> RouteEvents() =>
        [.. Silo.SentTo(HttpMethod.Post, RouteEventsPath).Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("event").GetString()!)];

    /// <summary>Lets every timer due within <paramref name="by"/> fire.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        Time.Advance(by);
        // Timer callbacks post asynchronous work; give it a moment to reach the scripted server.
        await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    /// <summary>Waits until <paramref name="condition"/> holds: work started by an event or a timer runs on its own.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting until " + what);
            }
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Answers with <paramref name="respond"/> once <paramref name="gate"/> completes: a request on its way.</summary>
    public static Func<Task<HttpResponseMessage>> After(Task gate, Func<Task<HttpResponseMessage>> respond) => async () =>
    {
        await gate;
        return await respond();
    };
}

/// <summary>Records the routes a playback opens; each gets its own loopback-looking URL.</summary>
internal sealed class FakeMediaProxy : IMediaProxy
{
    public List<FakeMediaRoute> Routes { get; } = [];

    public IMediaRoute Open(Uri upstream, MediaRouteScope scope, MediaCredentialSource credentials, MediaCredentials? initial = null)
    {
        var route = new FakeMediaRoute(new Uri($"http://127.0.0.1:1/route{Routes.Count + 1}/stream"), upstream, scope, credentials, initial);
        Routes.Add(route);
        return route;
    }
}

internal sealed record FakeMediaRoute(Uri LocalUrl, Uri Upstream, MediaRouteScope Scope, MediaCredentialSource Credentials, MediaCredentials? Initial)
    : IMediaRoute
{
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

/// <summary>Fixed media headers; records whether each request asked for fresh ones.</summary>
internal sealed class FakeMediaAuthorization : IMediaAuthorization
{
    public List<bool> Requests { get; } = [];

    public MediaAuthorizationException? FailWith { get; set; }

    public Task<MediaCredentials> GetAsync(ServerEntry server, bool rejected, CancellationToken cancellationToken)
    {
        Requests.Add(rejected);
        if (FailWith is { } failure)
        {
            throw failure;
        }
        string token = rejected ? "Bearer fresh" : "Bearer token";
        return Task.FromResult(new MediaCredentials([new("Authorization", token), new("X-Profile-Id", "7")], DateTimeOffset.MaxValue));
    }
}

internal sealed class FakeDeviceProbe : IPlaybackDeviceProbe
{
    public PlaybackDeviceFacts Facts { get; } = new()
    {
        Platform = "windows",
        HardwareVideoCodecs = ["h264", "hevc"],
        SoftwareVideoCodecs = ["h264", "mpeg2video"],
        OsVersion = "10.0.26300",
    };
}
