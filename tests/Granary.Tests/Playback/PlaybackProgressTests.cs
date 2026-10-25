using System.Net;
using System.Text.Json;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>Sequenced progress and the stop of one session (docs/api-contract.md, 10.1 to 10.3).</summary>
public sealed class PlaybackProgressTests
{
    private const string Session = PlaybackPayloads.SessionId;
    private const string ProgressPath = "api/v2/playback/" + Session + "/progress";
    private const string StopPath = "api/v2/playback/" + Session;

    private readonly ScriptedSilo _silo = new();
    private readonly LogSink _logs = new();

    private PlaybackProgress Create() => new(new PlaybackApi(_silo.Http), ScriptedSilo.Base, Session, PlaybackPayloads.InstallationId,
        _logs.For<PlaybackProgress>(), new PlaybackRetryPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5)));

    // The fixture's receipt names sequence 42, which would move the counter; this one only says applied.
    private static Func<Task<HttpResponseMessage>> Applied() => ScriptedSilo.Json(HttpStatusCode.OK, """{ "outcome": "applied" }""");

    private static JsonElement Body(SentRequest request) => JsonDocument.Parse(request.Body!).RootElement;

    [Fact]
    public async Task Report_SendsOneSequencePerSample()
    {
        _silo.On(HttpMethod.Post, ProgressPath, Applied());
        var progress = Create();

        Assert.Equal(ProgressOutcome.Reported, await progress.ReportAsync(12.5, isPaused: false));
        Assert.Equal(ProgressOutcome.Reported, await progress.ReportAsync(22.5, isPaused: true));

        var sent = _silo.SentTo(HttpMethod.Post, ProgressPath);
        Assert.Equal(2, sent.Count);
        Assert.Equal(1, Body(sent[0]).GetProperty("sequence").GetInt64());
        Assert.Equal(12.5, Body(sent[0]).GetProperty("position").GetDouble());
        Assert.False(Body(sent[0]).GetProperty("is_paused").GetBoolean());
        Assert.Equal(2, Body(sent[1]).GetProperty("sequence").GetInt64());
        Assert.True(Body(sent[1]).GetProperty("is_paused").GetBoolean());
        Assert.Equal(PlaybackPayloads.InstallationId, Body(sent[1]).GetProperty("installation_id").GetString());
    }

    [Fact]
    public async Task Report_ResendsTheIdenticalSample_AfterATransientFailure()
    {
        _silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Problem(503, "dependency_unavailable"), ScriptedSilo.NetworkError(), Applied());
        var progress = Create();

        Assert.Equal(ProgressOutcome.Reported, await progress.ReportAsync(40, isPaused: false));

        var bodies = _silo.SentTo(HttpMethod.Post, ProgressPath).Select(r => r.Body).ToList();
        Assert.Equal(3, bodies.Count);
        Assert.Single(bodies.Distinct());
    }

    [Fact]
    public async Task Report_GivesUpAfterThreeTries_AndTheNextSampleMovesOn()
    {
        _silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.NetworkError(), ScriptedSilo.NetworkError(), ScriptedSilo.NetworkError(), Applied());
        var progress = Create();

        Assert.Equal(ProgressOutcome.Unreported, await progress.ReportAsync(40, isPaused: false));
        Assert.Equal(ProgressOutcome.Reported, await progress.ReportAsync(50, isPaused: false));

        var sent = _silo.SentTo(HttpMethod.Post, ProgressPath);
        Assert.Equal(4, sent.Count);
        Assert.Equal(2, Body(sent[3]).GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task Report_CatchesUpWithANewerCommittedSample()
    {
        // stale_sample: the server already has sequence 7; the next sample must come after it.
        _silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Json(HttpStatusCode.OK, """
            { "outcome": "stale_sample", "accepted": { "sequence": 7, "position": 30, "is_paused": false } }
            """), Applied());
        var progress = Create();

        await progress.ReportAsync(20, isPaused: false);
        await progress.ReportAsync(25, isPaused: false);

        Assert.Equal(8, Body(_silo.SentTo(HttpMethod.Post, ProgressPath)[1]).GetProperty("sequence").GetInt64());
    }

    [Theory]
    [InlineData(404, "not_found", ProgressOutcome.SessionGone)]
    [InlineData(410, "playback_session_ended", ProgressOutcome.SessionEnded)]
    [InlineData(403, "permission_denied", ProgressOutcome.SessionEnded)]
    [InlineData(409, "installation_changed", ProgressOutcome.InstallationChanged)]
    public async Task Report_TellsWhatTheServerSaysAboutTheSession(int status, string code, ProgressOutcome expected)
    {
        _silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Problem(status, code));

        Assert.Equal(expected, await Create().ReportAsync(40, isPaused: false));
        Assert.Single(_silo.SentTo(HttpMethod.Post, ProgressPath));
    }

    [Fact]
    public async Task Stop_SendsTheFinalSampleAfterTheLastProgress()
    {
        _silo.On(HttpMethod.Post, ProgressPath, Applied());
        _silo.On(HttpMethod.Delete, StopPath, ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.StopCompleted));
        var progress = Create();
        await progress.ReportAsync(40, isPaused: false);

        await progress.StopAsync(55.5, isPaused: true);

        var body = Body(Assert.Single(_silo.SentTo(HttpMethod.Delete, StopPath)));
        Assert.Equal(2, body.GetProperty("sequence").GetInt64());
        Assert.Equal(55.5, body.GetProperty("position").GetDouble());
        Assert.True(body.GetProperty("is_paused").GetBoolean());
        Assert.True(Guid.TryParseExact(body.GetProperty("stop_id").GetString(), "D", out _));
        Assert.True(progress.IsStopped);
    }

    [Fact]
    public async Task Stop_RetriesWithTheSameStopId_UntilTheServerHasIt()
    {
        _silo.On(HttpMethod.Delete, StopPath, ScriptedSilo.NetworkError(), ScriptedSilo.Problem(503, "dependency_unavailable"),
            ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.StopReplayed));
        var progress = Create();

        await progress.StopAsync(55.5, isPaused: false);

        var bodies = _silo.SentTo(HttpMethod.Delete, StopPath).Select(r => r.Body).ToList();
        Assert.Equal(3, bodies.Count);
        Assert.Single(bodies.Distinct());
        Assert.True(progress.IsStopped);
    }

    [Fact]
    public async Task Stop_ASessionTheServerNoLongerKnows_IsOver()
    {
        _silo.On(HttpMethod.Delete, StopPath, ScriptedSilo.Problem(404, "not_found"));
        var progress = Create();

        await progress.StopAsync(55.5, isPaused: false);

        Assert.Single(_silo.SentTo(HttpMethod.Delete, StopPath));
        Assert.True(progress.IsStopped);
    }

    [Fact]
    public async Task Stop_HappensOnce_AndEndsTheReports()
    {
        _silo.On(HttpMethod.Delete, StopPath, ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.StopCompleted));
        var progress = Create();

        await Task.WhenAll(progress.StopAsync(10, isPaused: false), progress.StopAsync(20, isPaused: false));
        var late = await progress.ReportAsync(30, isPaused: false);

        Assert.Single(_silo.SentTo(HttpMethod.Delete, StopPath));
        Assert.Empty(_silo.SentTo(HttpMethod.Post, ProgressPath));
        Assert.Equal(ProgressOutcome.Unreported, late);
    }

    [Fact]
    public async Task Positions_AreNeverNegative()
    {
        _silo.On(HttpMethod.Post, ProgressPath, Applied());

        await Create().ReportAsync(-0.2, isPaused: false);

        Assert.Equal(0, Body(Assert.Single(_silo.SentTo(HttpMethod.Post, ProgressPath))).GetProperty("position").GetDouble());
    }
}
