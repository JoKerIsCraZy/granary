using System.Net;
using System.Text;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>Method, path, body and credentials of every playback call, against a server behind the path prefix <c>/silo</c>.</summary>
public sealed class PlaybackApiTests
{
    private const string Session = PlaybackPayloads.SessionId;

    private const string SessionPath = "api/v2/playback/" + Session;

    private readonly ScriptedSilo _silo = new();

    private PlaybackApi Api => new(_silo.Http);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SentRequest Only(HttpMethod method, string path)
    {
        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(method, sent.Method);
        Assert.Equal("/silo/" + path, sent.Uri.PathAndQuery);
        Assert.Equal(SiloAuth.Profile, sent.Auth);
        return sent;
    }

    private static Func<Task<HttpResponseMessage>> RawProblem(HttpStatusCode status, string json) =>
        () => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/problem+json") });

    [Fact]
    public async Task GetCapabilities_ReadsTheDocumentForTheProfile()
    {
        _silo.On(HttpMethod.Get, "api/v2/playback/capabilities", ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.CapabilityAvailable));

        var capabilities = await Api.GetCapabilitiesAsync(ScriptedSilo.Base, Ct);

        Only(HttpMethod.Get, "api/v2/playback/capabilities");
        Assert.Equal(PlaybackPayloads.InstallationId, capabilities.InstallationId);
        Assert.True(capabilities.IsUsable);
    }

    [Fact]
    public async Task Start_PostsTheBodyAndReadsTheCreatedDecision()
    {
        _silo.On(HttpMethod.Post, "api/v2/playback/start", ScriptedSilo.Json(HttpStatusCode.Created, PlaybackPayloads.StartOpaqueIds));
        var request = PlaybackRequests.Start();

        var decision = await Api.StartAsync(ScriptedSilo.Base, request, Ct);

        var sent = Only(HttpMethod.Post, "api/v2/playback/start");
        Assert.Equal(PlaybackRequests.Json(request), sent.Body);
        Assert.Equal(PlaybackOutcomes.Playable, decision.Outcome);
        Assert.Equal("plan:478677870860e5e5108c18bff749b34b", decision.PlaybackPlan!.PlanId);
    }

    [Fact]
    public async Task Start_InstallationChangedSurfacesItsCode()
    {
        _silo.On(HttpMethod.Post, "api/v2/playback/start", RawProblem(HttpStatusCode.Conflict, PlaybackPayloads.InstallationChanged));

        var error = await Assert.ThrowsAsync<SiloApiException>(() => Api.StartAsync(ScriptedSilo.Base, PlaybackRequests.Start(), Ct));

        Assert.Equal((409, ProblemCodes.InstallationChanged), (error.Status, error.Code));
    }

    [Fact]
    public async Task Start_ValidationProblemSurfacesItsErrors()
    {
        _silo.On(HttpMethod.Post, "api/v2/playback/start", RawProblem(HttpStatusCode.UnprocessableEntity, PlaybackPayloads.InvalidProtocol));

        var error = await Assert.ThrowsAsync<SiloApiException>(() => Api.StartAsync(ScriptedSilo.Base, PlaybackRequests.Start(), Ct));

        Assert.Equal(ProblemCodes.ValidationFailed, error.Code);
        Assert.Equal("body", Assert.Single(error.Problem.Errors!).Location);
    }

    [Fact]
    public async Task Replan_PostsUnderTheSession()
    {
        _silo.On(HttpMethod.Post, SessionPath + "/replan", ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.ReplanReanchored));
        var request = PlaybackRequests.Replan();

        var decision = await Api.ReplanAsync(ScriptedSilo.Base, Session, request, Ct);

        Assert.Equal(PlaybackRequests.Json(request), Only(HttpMethod.Post, SessionPath + "/replan").Body);
        Assert.Equal("v3:f0144c47fa349e3e", decision.PlaybackPlan!.PlanAttemptKey);
    }

    [Theory]
    [InlineData(404, ProblemCodes.NotFound)]
    [InlineData(409, ProblemCodes.Conflict)]
    [InlineData(410, ProblemCodes.PlaybackSessionEnded)]
    public async Task Replan_ProblemsSurfaceTheirCode(int status, string code)
    {
        _silo.On(HttpMethod.Post, SessionPath + "/replan", ScriptedSilo.Problem(status, code));

        var error = await Assert.ThrowsAsync<SiloApiException>(() => Api.ReplanAsync(ScriptedSilo.Base, Session, PlaybackRequests.Replan(), Ct));

        Assert.Equal((status, code), (error.Status, error.Code));
    }

    [Fact]
    public async Task ReportProgress_PostsTheSampleAndReadsTheReceipt()
    {
        _silo.On(HttpMethod.Post, SessionPath + "/progress",
            ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.ProgressApplied),
            ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.ProgressStale));
        var request = PlaybackProgressRequest.For(PlaybackPayloads.InstallationId, new PlaybackSample(42, 120, false));

        var applied = await Api.ReportProgressAsync(ScriptedSilo.Base, Session, request, Ct);
        var stale = await Api.ReportProgressAsync(ScriptedSilo.Base, Session, request, Ct);

        Assert.Equal($$"""{"installation_id":"{{PlaybackPayloads.InstallationId}}","sequence":42,"position":120,"is_paused":false}""", _silo.Sent[0].Body);
        Assert.Equal(PlaybackMutationOutcomes.Applied, applied.Outcome);
        Assert.Equal(new PlaybackSample(42, 120, false), applied.Accepted);
        Assert.Equal(PlaybackMutationOutcomes.StaleSample, stale.Outcome);
        Assert.Null(stale.StopId);
    }

    [Theory]
    [InlineData(404, ProblemCodes.NotFound)]
    [InlineData(410, ProblemCodes.PlaybackSessionEnded)]
    [InlineData(409, ProblemCodes.InstallationChanged)]
    [InlineData(403, ProblemCodes.PermissionDenied)]
    [InlineData(503, ProblemCodes.DependencyUnavailable)]
    public async Task ReportProgress_ProblemsSurfaceTheirCode(int status, string code)
    {
        _silo.On(HttpMethod.Post, SessionPath + "/progress", ScriptedSilo.Problem(status, code));

        var error = await Assert.ThrowsAsync<SiloApiException>(() =>
            Api.ReportProgressAsync(ScriptedSilo.Base, Session, PlaybackProgressRequest.For(PlaybackPayloads.InstallationId, new PlaybackSample(1, 0, true)), Ct));

        Assert.Equal((status, code), (error.Status, error.Code));
    }

    [Fact]
    public async Task ReportProgress_ConflictFixtureSurfacesItsCode()
    {
        _silo.On(HttpMethod.Post, SessionPath + "/progress", RawProblem(HttpStatusCode.Conflict, PlaybackPayloads.ProgressConflict));

        var error = await Assert.ThrowsAsync<SiloApiException>(() =>
            Api.ReportProgressAsync(ScriptedSilo.Base, Session, PlaybackProgressRequest.For(PlaybackPayloads.InstallationId, new PlaybackSample(42, 121, false)), Ct));

        Assert.Equal(ProblemCodes.ProgressConflict, error.Code);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(1, -1.0)]
    [InlineData(1, double.NaN)]
    [InlineData(1, double.PositiveInfinity)]
    public async Task ReportProgress_RefusesSamplesTheServerWouldReject(long sequence, double position)
    {
        var request = PlaybackProgressRequest.For(PlaybackPayloads.InstallationId, new PlaybackSample(sequence, position, false));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Api.ReportProgressAsync(ScriptedSilo.Base, Session, request, Ct));
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task Stop_DeletesWithAJsonBodyAndReadsTheReceipt()
    {
        _silo.On(HttpMethod.Delete, SessionPath, ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.StopCompleted));
        var request = new PlaybackStopRequest
        {
            InstallationId = PlaybackPayloads.InstallationId,
            StopId = "44444444-4444-4444-8444-444444444444",
            FinalSample = new PlaybackSample(43, 125.5, true),
        };

        var receipt = await Api.StopAsync(ScriptedSilo.Base, Session, request, Ct);

        var sent = Only(HttpMethod.Delete, SessionPath);
        Assert.Equal($$"""{"installation_id":"{{PlaybackPayloads.InstallationId}}","stop_id":"44444444-4444-4444-8444-444444444444","sequence":43,"position":125.5,"is_paused":true}""",
            sent.Body);
        var handled = Assert.Single(_silo.Handler.Requests);
        Assert.Equal("application/json", handled.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("application/json", Assert.Single(handled.Headers.Accept).MediaType);
        Assert.Equal(PlaybackMutationOutcomes.Stopped, receipt.Outcome);
        Assert.Equal("44444444-4444-4444-8444-444444444444", receipt.StopId);
        Assert.Equal("33333333-3333-4333-8333-333333333333", receipt.HistoryId);
        Assert.Equal(new PlaybackSample(42, 120, false), receipt.Accepted);
    }

    [Fact]
    public async Task Stop_WithoutAFinalSampleSendsOnlyTheIds()
    {
        _silo.On(HttpMethod.Delete, SessionPath, ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.StopReplayed));

        var receipt = await Api.StopAsync(ScriptedSilo.Base, Session,
            new PlaybackStopRequest { InstallationId = PlaybackPayloads.InstallationId, StopId = "22222222-2222-4222-8222-222222222222" }, Ct);

        Assert.Equal($$"""{"installation_id":"{{PlaybackPayloads.InstallationId}}","stop_id":"22222222-2222-4222-8222-222222222222"}""", _silo.Sent[0].Body);
        Assert.Equal(PlaybackMutationOutcomes.Replayed, receipt.Outcome);
    }

    [Fact]
    public async Task Stop_UnknownSessionIsNotFound()
    {
        _silo.On(HttpMethod.Delete, SessionPath, ScriptedSilo.Problem(404, ProblemCodes.NotFound));

        var error = await Assert.ThrowsAsync<SiloApiException>(() => Api.StopAsync(ScriptedSilo.Base, Session,
            new PlaybackStopRequest { InstallationId = PlaybackPayloads.InstallationId, StopId = "22222222-2222-4222-8222-222222222222" }, Ct));

        Assert.Equal(ProblemCodes.NotFound, error.Code);
    }

    [Fact]
    public async Task SendRouteEvent_PostsTheEventAndReadsTheAcceptedReceipt()
    {
        _silo.On(HttpMethod.Post, "api/v2/playback/route-events", ScriptedSilo.Json(HttpStatusCode.Accepted, PlaybackPayloads.RouteEventAccepted));
        var routeEvent = PlaybackRequests.FirstFrame();

        var receipt = await Api.SendRouteEventAsync(ScriptedSilo.Base, routeEvent, Ct);

        Assert.Equal(PlaybackRequests.Json(routeEvent), Only(HttpMethod.Post, "api/v2/playback/route-events").Body);
        Assert.Equal(("55555555-5555-4555-8555-555555555555", "accepted"), (receipt.EventId, receipt.Outcome));
    }

    [Fact]
    public async Task SendRouteEvent_RateLimitIsAProblemToDrop()
    {
        _silo.On(HttpMethod.Post, "api/v2/playback/route-events", ScriptedSilo.Problem(429, ProblemCodes.RateLimited, TimeSpan.FromSeconds(30)));

        var error = await Assert.ThrowsAsync<SiloApiException>(() => Api.SendRouteEventAsync(ScriptedSilo.Base, PlaybackRequests.FirstFrame(), Ct));

        Assert.Equal(ProblemCodes.RateLimited, error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("..")]
    public async Task SessionCalls_RefuseIdsThatAreNotOne(string sessionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Api.ReplanAsync(ScriptedSilo.Base, sessionId, PlaybackRequests.Replan(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Api.ReportProgressAsync(ScriptedSilo.Base, sessionId,
            PlaybackProgressRequest.For(PlaybackPayloads.InstallationId, new PlaybackSample(1, 0, false)), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Api.StopAsync(ScriptedSilo.Base, sessionId,
            new PlaybackStopRequest { InstallationId = PlaybackPayloads.InstallationId, StopId = "s" }, Ct));
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task SessionCalls_EscapeTheSessionId()
    {
        _silo.On(HttpMethod.Post, "api/v2/playback/a%2Fb/progress", ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.ProgressApplied));

        await Api.ReportProgressAsync(ScriptedSilo.Base, "a/b", PlaybackProgressRequest.For(PlaybackPayloads.InstallationId, new PlaybackSample(1, 0, false)), Ct);

        Assert.Equal("/silo/api/v2/playback/a%2Fb/progress", Assert.Single(_silo.Sent).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Replan_RefusesBodiesTheServerWouldReject()
    {
        var valid = PlaybackRequests.Replan();

        await Assert.ThrowsAsync<ArgumentException>(() => Api.ReplanAsync(ScriptedSilo.Base, Session, valid with { Failure = null }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Api.ReplanAsync(ScriptedSilo.Base, Session,
            valid with { Operation = ReplanOperations.TrackChange }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Api.ReplanAsync(ScriptedSilo.Base, Session, valid with { Operation = " " }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Api.ReplanAsync(ScriptedSilo.Base, Session,
            valid with { Operation = ReplanOperations.QualityChange, Failure = null, QualityPreference = " " }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Api.ReplanAsync(ScriptedSilo.Base, Session, valid with { AttemptCount = 9 }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Api.ReplanAsync(ScriptedSilo.Base, Session, valid with { AttemptCount = 0 }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Api.ReplanAsync(ScriptedSilo.Base, Session, valid with { PositionSeconds = -1 }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Api.ReplanAsync(ScriptedSilo.Base, Session,
            valid with { AttemptedPlanKeys = [.. Enumerable.Range(0, 17).Select(i => $"v3:{i:D8}")] }, Ct));
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task Replan_IntentOperationsGoOutWithoutAFailure()
    {
        _silo.On(HttpMethod.Post, SessionPath + "/replan", ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.ReplanReanchored));

        await Api.ReplanAsync(ScriptedSilo.Base, Session, PlaybackRequests.Replan() with { Operation = ReplanOperations.SeekReanchor, Failure = null }, Ct);

        Assert.DoesNotContain("\"failure\"", Assert.Single(_silo.Sent).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendRouteEvent_RefusesMoreDiagnosticsThanTheServerTakes()
    {
        var crowded = PlaybackRequests.FirstFrame() with
        {
            Diagnostics = Enumerable.Range(0, 33).ToDictionary(i => $"k{i}", i => "v"),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => Api.SendRouteEventAsync(ScriptedSilo.Base, crowded, Ct));
        Assert.Empty(_silo.Sent);
    }
}
