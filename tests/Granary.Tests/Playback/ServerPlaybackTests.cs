using System.Net;
using Granary.Core.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>One server playback from start to stop (docs/api-contract.md, 8 to 10).</summary>
public sealed class ServerPlaybackTests
{
    private readonly ServerPlaybackFixture _fixture = new();

    // Start

    [Fact]
    public async Task Start_LoadsThePlanWithTheMediaHeaders_AtThePlansStart()
    {
        var playback = await _fixture.StartAsync();

        var load = Assert.Single(_fixture.Engine.Loaded);
        Assert.Equal(StreamUrl(), load.Url);
        Assert.Equal([new("Authorization", "Bearer token"), new("X-Profile-Id", "7")], load.Headers);
        Assert.Equal(TimeSpan.FromSeconds(12.5), load.Start);
        Assert.Equal(ServerPlaybackPhase.Preparing, playback.Status.Phase);
        Assert.Equal("Heat", playback.Title);
        Assert.Equal("1995", playback.Subtitle);
    }

    [Fact]
    public async Task Start_AsksForTheResumePoint_TheVersionAndItsAudio()
    {
        await _fixture.StartAsync();

        var body = _fixture.Body(HttpMethod.Post, StartPath);
        Assert.Equal("42", body.GetProperty("file_id").GetString());
        Assert.Equal("7", body.GetProperty("profile_id").GetString());
        Assert.Equal(PlaybackPayloads.InstallationId, body.GetProperty("installation_id").GetString());
        // 1325.5 s minus the 7 s resume rewind.
        Assert.Equal(1318.5, body.GetProperty("start_position").GetDouble());
        Assert.Equal("auto", body.GetProperty("quality_preference").GetString());
        Assert.True(Guid.TryParse(body.GetProperty("playback_attempt_id").GetString(), out _));
        Assert.Contains("header_authenticated_media_v1", body.GetProperty("client_features").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task StartOver_AsksForTheBeginning()
    {
        await _fixture.StartAsync(PlayRequest.FromStart(MovieId));

        Assert.Equal(0, _fixture.Body(HttpMethod.Post, StartPath).GetProperty("start_position").GetDouble());
    }

    [Fact]
    public async Task Start_UsesTheProfilesQualityAndBitrateCap()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/settings/values/effective", Ok("""
            { "items": [ { "key": "playback.preferred_quality", "value": "720p" }, { "key": "playback.max_bitrate_kbps", "value": 6000 } ], "revision": 2 }
            """));

        var playback = await _fixture.StartAsync();

        var body = _fixture.Body(HttpMethod.Post, StartPath);
        Assert.Equal("720p", body.GetProperty("quality_preference").GetString());
        Assert.Equal(6000, body.GetProperty("bandwidth_cap_kbps").GetInt32());
        Assert.Equal("720p", playback.Preferences.Quality);
    }

    [Fact]
    public async Task ATerminalDecision_ShowsTheServersMessage()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.TerminalDecision));

        var playback = await _fixture.StartAsync();

        Assert.Equal(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, "The stream could not be prepared.", CanRetry: true), playback.Status);
        Assert.Empty(_fixture.Engine.Loaded);
        Assert.Contains("terminal", _fixture.RouteEvents());
    }

    [Fact]
    public async Task AMissingTitle_IsExplained()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, ScriptedSilo.Problem(404, "not_found"));

        var playback = await _fixture.StartAsync();

        Assert.Equal(ServerPlaybackPhase.Failed, playback.Status.Phase);
        Assert.Equal("This title is no longer available.", playback.Status.Message);
        Assert.False(playback.Status.CanRetry);
    }

    [Fact]
    public async Task AServerWithoutProtocol3_CannotPlay()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/playback/capabilities", Ok(PlaybackPayloads.CapabilityUnconfigured));

        var playback = await _fixture.StartAsync();

        Assert.Equal(ServerPlaybackPhase.Failed, playback.Status.Phase);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, StartPath));
    }

    [Fact]
    public async Task AReinstalledServer_IsAskedAgainOnce()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, ScriptedSilo.Problem(409, "installation_changed"), Created(PlaybackPayloads.StartOpaqueIds));

        await _fixture.StartAsync();

        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, "api/v2/playback/capabilities").Count);
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Post, StartPath).Count);
        Assert.Single(_fixture.Engine.Loaded);
    }

    [Fact]
    public async Task WithoutALogin_NothingIsLoaded()
    {
        _fixture.Auth.FailWith = new MediaAuthorizationException(MediaAuthorizationFailure.SignedOut, "Sign in to play from this server.");

        var playback = await _fixture.StartAsync();

        Assert.Equal(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, "Sign in to play from this server.", CanRetry: false), playback.Status);
        Assert.Empty(_fixture.Engine.Loaded);
    }

    // Playing and progress

    [Fact]
    public async Task TheFirstFrame_MeansPlaying()
    {
        var playback = await _fixture.PlayingAsync();

        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
        Assert.Equal(["plan_selected", "first_frame"], _fixture.RouteEvents());
    }

    [Fact]
    public async Task Progress_IsReportedEveryTenSeconds_InSourceTime()
    {
        await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(20));

        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));
        _fixture.Engine.RaiseState(Frame(30));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));

        var sent = _fixture.Silo.SentTo(HttpMethod.Post, ProgressPath);
        Assert.Equal(2, sent.Count);
        Assert.Equal(20, _fixture.Body(HttpMethod.Post, ProgressPath).GetProperty("position").GetDouble());
        Assert.Equal(30, _fixture.Body(HttpMethod.Post, ProgressPath, 1).GetProperty("position").GetDouble());
    }

    [Fact]
    public async Task APause_IsReportedAtOnce()
    {
        await _fixture.PlayingAsync();

        _fixture.Engine.RaiseState(Frame(20, paused: true));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.True(_fixture.Body(HttpMethod.Post, ProgressPath).GetProperty("is_paused").GetBoolean());
    }

    [Fact]
    public async Task ASessionEndedByTheServer_EndsPlayback()
    {
        _fixture.Silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Problem(410, "playback_session_ended"));
        var playback = await _fixture.PlayingAsync();

        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ServerPlaybackPhase.Failed, playback.Status.Phase);
        Assert.False(playback.Status.CanRetry);
        Assert.Equal(1, _fixture.Engine.Stops);
    }

    [Fact]
    public async Task ASessionTheServerForgot_StartsAgainWhereTheViewerIs()
    {
        _fixture.Silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Problem(404, "not_found"));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(300));

        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));

        var restart = _fixture.Body(HttpMethod.Post, StartPath, 1);
        Assert.Equal(300, restart.GetProperty("start_position").GetDouble());
        Assert.NotEqual(_fixture.Body(HttpMethod.Post, StartPath).GetProperty("playback_attempt_id").GetString(),
            restart.GetProperty("playback_attempt_id").GetString());
        Assert.Equal(StreamUrl(OtherSession), _fixture.Engine.Loaded[^1].Url);
    }

    // Stop and the end

    [Fact]
    public async Task Stop_SendsTheFinalPosition()
    {
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(321.5, paused: true));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await playback.StopAsync();

        var stop = _fixture.Body(HttpMethod.Delete, StopPath);
        Assert.Equal(321.5, stop.GetProperty("position").GetDouble());
        Assert.True(stop.GetProperty("is_paused").GetBoolean());
        Assert.Contains("stopped", _fixture.RouteEvents());
    }

    [Fact]
    public async Task AfterTheStop_NothingIsReportedAnyMore()
    {
        var playback = await _fixture.PlayingAsync();
        await playback.StopAsync();

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "stream closed"));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ProgressPath));
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ReplanPath));
        Assert.Single(_fixture.Engine.Loaded);
    }

    [Fact]
    public async Task TheEnd_StopsTheSessionAtTheEnd()
    {
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(10195));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(ServerPlaybackPhase.Ended, playback.Status.Phase);
        Assert.Equal(10195, _fixture.Body(HttpMethod.Delete, StopPath).GetProperty("position").GetDouble());
    }

    // Recovery

    [Fact]
    public async Task ARejectedToken_IsRenewed_AndTheStreamReloadedWhereItWas()
    {
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(400));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, null));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal([false, true], _fixture.Auth.Requests);
        var reload = _fixture.Engine.Loaded[^1];
        Assert.Equal(StreamUrl(), reload.Url);
        Assert.Equal(TimeSpan.FromSeconds(400), reload.Start);
        Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer fresh"), reload.Headers);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ReplanPath));
        Assert.NotEqual(ServerPlaybackPhase.Failed, playback.Status.Phase);
    }

    [Fact]
    public async Task APlanThatFailsToLoad_IsReplannedWithItsKey()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.TranscodeDecision.Replace(OtherSession, Session, StringComparison.Ordinal)));
        var playback = await _fixture.StartAsync();

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "loading failed"));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var replan = _fixture.Body(HttpMethod.Post, ReplanPath);
        Assert.Equal("failure_recovery", replan.GetProperty("operation").GetString());
        Assert.Equal("plan:478677870860e5e5108c18bff749b34b", replan.GetProperty("failed_plan_id").GetString());
        Assert.Equal(["v3:f0144c47fa349e3e"], replan.GetProperty("attempted_plan_keys").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(1, replan.GetProperty("attempt_count").GetInt32());
        Assert.False(string.IsNullOrEmpty(replan.GetProperty("failure").GetProperty("classification").GetString()));
        // The transcode plays from player time 2 s; its stream starts at source 598 s.
        var load = _fixture.Engine.Loaded[^1];
        Assert.EndsWith("/master.m3u8", load.Url.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(2), load.Start);
        Assert.Equal(598, playback.TimelineOffsetSeconds);
    }

    [Fact]
    public async Task ALostConnection_IsWaitedFor_AndTheStreamComesBackWhereItWas()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, ScriptedSilo.NetworkError(), Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(700));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(ServerPlaybackPhase.Reconnecting, playback.Status.Phase);

        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(2));

        var replans = _fixture.Silo.SentTo(HttpMethod.Post, ReplanPath);
        Assert.Equal(2, replans.Count);
        var body = _fixture.Body(HttpMethod.Post, ReplanPath, 1);
        Assert.Equal("track_change", body.GetProperty("operation").GetString());
        Assert.False(body.TryGetProperty("failure", out _));
        Assert.Equal(700, body.GetProperty("position_seconds").GetDouble());
        Assert.Equal(TimeSpan.FromSeconds(700), _fixture.Engine.Loaded[^1].Start);
        _fixture.Engine.RaiseState(Frame(700));
        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
    }

    [Fact]
    public async Task ALostSession_AfterTheConnectionCameBack_IsStartedAgain()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, ScriptedSilo.Problem(404, "not_found"));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(700));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(700, _fixture.Body(HttpMethod.Post, StartPath, 1).GetProperty("start_position").GetDouble());
        Assert.Equal(StreamUrl(OtherSession), _fixture.Engine.Loaded[^1].Url);
    }

    [Fact]
    public async Task AnEndFarFromTheEnd_IsALostConnection()
    {
        // mpv reports a stream that broke off as its end; the plan's source runs 7200 s, so 5000 s is not its end.
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(5000));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(ServerPlaybackPhase.Reconnecting, playback.Status.Phase);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Delete, StopPath));
    }

    // Seeking

    [Fact]
    public async Task Seek_InADirectPlay_IsTheEnginesOwn()
    {
        var playback = await _fixture.PlayingAsync();

        playback.Seek(3000);

        Assert.Equal([TimeSpan.FromSeconds(3000)], _fixture.Engine.Seeks);
    }

    [Fact]
    public async Task Seek_OutsideAStreamsWindow_AsksTheServerToReanchor()
    {
        // The transcode plays source time 598 s onwards and cannot seek anywhere.
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.TranscodeDecision));
        _fixture.Silo.On(HttpMethod.Post, "api/v2/playback/" + OtherSession + "/replan", Ok(StartedAgain));
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(2));

        playback.Seek(100);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var replan = _fixture.Body(HttpMethod.Post, "api/v2/playback/" + OtherSession + "/replan");
        Assert.Equal("seek_reanchor", replan.GetProperty("operation").GetString());
        Assert.Equal(100, replan.GetProperty("position_seconds").GetDouble());
        Assert.Empty(_fixture.Engine.Seeks);
        Assert.Equal(2, _fixture.Engine.Loaded.Count);
    }

    [Fact]
    public async Task Seek_InsideAStreamsWindow_StaysLocal()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.TranscodeDecision));
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(2));

        // Back to source 599 s: inside what the stream already holds, 1 s of player time.
        playback.Seek(599);

        Assert.Equal([TimeSpan.FromSeconds(1)], _fixture.Engine.Seeks);
    }
}
