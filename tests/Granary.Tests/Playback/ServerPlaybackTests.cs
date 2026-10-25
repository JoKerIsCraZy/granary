using System.Net;
using System.Text.RegularExpressions;
using Granary.Core.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>One server playback from start to stop (docs/api-contract.md, 8 to 10).</summary>
public sealed class ServerPlaybackTests
{
    private readonly ServerPlaybackFixture _fixture = new();

    // The transcode, as the answer to a replan of the first session.
    private static string TranscodeInThisSession => PlaybackPayloads.TranscodeDecision.Replace(OtherSession, Session, StringComparison.Ordinal);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

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

    [Theory]
    [InlineData("{ \"id\": \"file:42:audio:0\", \"index\": 0 }", 1)]
    [InlineData("{ \"id\": \"file:42:audio:1\", \"index\": 1 }", 2)]
    [InlineData("{ \"id\": \"file:42:audio:1\" }", 2)]
    public async Task ADirectPlay_PlaysTheAudioThePlanSelected(string audio, int stream)
    {
        // The file holds every audio track, and mpv on its own plays the one the file marks as default.
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(WithASecondAudioTrack(PlaybackPayloads.WatchStateOk)));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(SelectingAudio(audio)));

        await _fixture.StartAsync();

        Assert.Equal(stream, Assert.Single(_fixture.Engine.Loaded).AudioStream);
    }

    [Fact]
    public async Task AnAudioTrackTheFileLacks_IsReportedForAnotherRoute()
    {
        // The claim says the ordinal maps onto the file; mpv would play an absent stream silent.
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(SelectingAudio("{ \"id\": \"file:42:audio:3\", \"index\": 3 }")));
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(TranscodeInThisSession));
        var playback = await _fixture.StartAsync();
        await playback.Recovery;

        var replan = _fixture.Body(HttpMethod.Post, ReplanPath);
        Assert.Equal("failure_recovery", replan.GetProperty("operation").GetString());
        Assert.Equal("audio_track_unavailable", replan.GetProperty("failure").GetProperty("classification").GetString());
        Assert.Null(Assert.Single(_fixture.Engine.Loaded).AudioStream);
    }

    // The first "audio" object of the decision is selected_tracks.audio; the claims have one too.
    private static string SelectingAudio(string audio)
    {
        string decision = new Regex(@"(""audio"":\s*)\{[^}]*\}").Replace(PlaybackPayloads.StartOpaqueIds, "$1" + audio, 1);
        Assert.Contains(audio, decision, StringComparison.Ordinal);
        return decision;
    }

    private static string WithASecondAudioTrack(string watch)
    {
        string twoTracks = new Regex(@"(""audio_tracks"":\s*\[\s*\{[^}]*\})")
            .Replace(watch, "$1, { \"language\": \"ger\", \"codec\": \"eac3\", \"channels\": 6, \"default\": false }", 1);
        Assert.Contains("\"ger\"", twoTracks, StringComparison.Ordinal);
        return twoTracks;
    }

    [Fact]
    public async Task AStreamTheServerMade_CarriesItsAudioAlready()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.TranscodeDecision));

        await _fixture.StartAsync();

        Assert.Null(Assert.Single(_fixture.Engine.Loaded).AudioStream);
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
    public async Task AnEngineThatRefusesTheLoad_EndsInAFailure()
    {
        _fixture.Engine.FailNextLoad = new ArgumentException("Header 'X-Bad' cannot be passed to mpv safely.");

        var playback = await _fixture.StartAsync();

        Assert.Equal(ServerPlaybackPhase.Failed, playback.Status.Phase);
        Assert.True(playback.Status.CanRetry);
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
    public async Task APause_IsReportedOnceItHolds()
    {
        await _fixture.PlayingAsync();

        _fixture.Engine.RaiseState(Frame(20, paused: true));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.True(_fixture.Body(HttpMethod.Post, ProgressPath).GetProperty("is_paused").GetBoolean());
    }

    [Fact]
    public async Task ThePauseOfAStreamThatBrokeOff_IsNoPause()
    {
        // mpv pauses a moment before it says the stream ended (keep-open); the viewer paused nothing.
        await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(400));

        _fixture.Engine.RaiseState(Frame(400, paused: true));
        _fixture.Engine.RaiseState(Frame(7200, paused: true) with { IsAtEnd = true });
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, null));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ProgressPath));
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

        await playback.StopAsync();

        var stop = _fixture.Body(HttpMethod.Delete, StopPath);
        Assert.Equal(321.5, stop.GetProperty("position").GetDouble());
        Assert.True(stop.GetProperty("is_paused").GetBoolean());
        Assert.Contains("stopped", _fixture.RouteEvents());
    }

    [Fact]
    public async Task AStopBeforeTheFirstFrame_LeavesTheResumePointAlone()
    {
        // The load starts at 12.5 s, but the viewer saw nothing: 0 is never stored, so the resume point stays.
        var playback = await _fixture.StartAsync();

        await playback.StopAsync();

        Assert.Equal(0, _fixture.Body(HttpMethod.Delete, StopPath).GetProperty("position").GetDouble());
    }

    [Fact]
    public async Task AStopWhileTheServerStarts_EndsTheSessionItMakes()
    {
        var answer = new TaskCompletionSource();
        _fixture.Silo.On(HttpMethod.Post, StartPath, After(answer.Task, Created(PlaybackPayloads.StartOpaqueIds)));
        var playback = _fixture.Create();
        var starting = playback.StartAsync(PlayRequest.Resume(MovieId), Cancellation);
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Post, StartPath).Count == 1, "the start is sent");

        var stop = playback.StopAsync();
        answer.SetResult();
        await starting;
        await stop;

        Assert.Empty(_fixture.Engine.Loaded);
        Assert.Equal(0, _fixture.Body(HttpMethod.Delete, StopPath).GetProperty("position").GetDouble());
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
    public async Task TheEnd_ShowsBeforeTheStopIsThrough()
    {
        var answer = new TaskCompletionSource();
        _fixture.Silo.On(HttpMethod.Delete, StopPath, After(answer.Task, Ok(PlaybackPayloads.StopCompleted)));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(10195));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));

        await Until(() => playback.Status.Phase == ServerPlaybackPhase.Ended, "the end is shown");
        answer.SetResult();
        await playback.StopAsync();
    }

    [Fact]
    public async Task TheEnd_StopsTheSessionAtTheEnd()
    {
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(10195));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));
        await playback.Recovery;

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
        await playback.Recovery;

        Assert.Equal([false, true], _fixture.Auth.Requests);
        var reload = _fixture.Engine.Loaded[^1];
        Assert.Equal(StreamUrl(), reload.Url);
        Assert.Equal(TimeSpan.FromSeconds(400), reload.Start);
        Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer fresh"), reload.Headers);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ReplanPath));
        Assert.NotEqual(ServerPlaybackPhase.Failed, playback.Status.Phase);
    }

    [Fact]
    public async Task TheEndOfARejectedStream_IsNotWhereTheViewerIs()
    {
        // mpv takes a 401 mid-stream for the end of the file: it jumps to the end and pauses (keep-open).
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(400));

        _fixture.Engine.RaiseState(Frame(7200, paused: true) with { IsAtEnd = true });
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, null));
        await playback.Recovery;

        Assert.Equal(TimeSpan.FromSeconds(400), _fixture.Engine.Loaded[^1].Start);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ProgressPath));
    }

    [Fact]
    public async Task ASecondRejection_ReloadsWhereTheFirstReloadStarted()
    {
        await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(400));
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, null));
        await Until(() => _fixture.Engine.Loaded.Count == 2, "the first reload");

        // Refused again before its first frame: the viewer is still at 400 s, not at the plan's start.
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, null));

        await Until(() => _fixture.Engine.Loaded.Count == 3, "the second reload");
        Assert.Equal(TimeSpan.FromSeconds(400), _fixture.Engine.Loaded[^1].Start);
    }

    [Fact]
    public async Task AStateFromTheLoadBefore_IsNoFirstFrame()
    {
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(400));
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, null));
        await playback.Recovery;

        // Raised by the first load before the reload, it arrives after it.
        _fixture.Engine.RaiseState(Frame(500) with { LoadId = 1 });
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ProgressPath));
    }

    [Fact]
    public async Task AReloadDuringASeek_StartsAtTheSeeksTarget()
    {
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(400));

        playback.Seek(3000);
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, null));
        await playback.Recovery;

        Assert.Equal(TimeSpan.FromSeconds(3000), _fixture.Engine.Loaded[^1].Start);
    }

    [Fact]
    public async Task APlanThatFailsToLoad_IsReplannedWithItsKey()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.TranscodeDecision.Replace(OtherSession, Session, StringComparison.Ordinal)));
        var playback = await _fixture.StartAsync();

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "loading failed"));
        await playback.Recovery;

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
        // The plan's own headers (X-Test here) are not sent: the client sends only the media headers it knows.
        Assert.DoesNotContain(load.Headers, h => h.Key == "X-Test");
    }

    [Fact]
    public async Task AReplanThatMeetsABusyServer_IsSentAgainUnchanged()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, ScriptedSilo.Problem(503, "dependency_unavailable"), Ok(TranscodeInThisSession));
        var playback = await _fixture.StartAsync();

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "loading failed"));
        await _fixture.Time.FireNextTimerAsync(Cancellation);
        await playback.Recovery;

        var first = _fixture.Body(HttpMethod.Post, ReplanPath);
        var again = _fixture.Body(HttpMethod.Post, ReplanPath, 1);
        Assert.Equal(first.GetProperty("replan_request_id").GetString(), again.GetProperty("replan_request_id").GetString());
        Assert.Equal(2, _fixture.Engine.Loaded.Count);
        Assert.NotEqual(ServerPlaybackPhase.Failed, playback.Status.Phase);
    }

    [Fact]
    public async Task ALoadThatShowsNoPicture_IsGivenUp_AndReplanned()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(TranscodeInThisSession));
        var playback = await _fixture.StartAsync();

        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(90));
        await playback.Recovery;

        Assert.Equal(1, _fixture.Engine.Stops);
        Assert.Equal("failure_recovery", _fixture.Body(HttpMethod.Post, ReplanPath).GetProperty("operation").GetString());
        Assert.Equal(2, _fixture.Engine.Loaded.Count);
    }

    [Fact]
    public async Task ALostConnection_IsWaitedFor_AndTheStreamComesBackWhereItWas()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, ScriptedSilo.NetworkError(), Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(700));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        Assert.Equal(ServerPlaybackPhase.Reconnecting, playback.Status.Phase);

        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(2));
        await playback.Recovery;

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
    public async Task AReloadThatBreaksAfterAReconnect_KeepsReconnecting()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(700));
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await playback.Recovery;
        Assert.Equal(2, _fixture.Engine.Loaded.Count);

        // The stream the server gave back breaks off before its first frame: the network is not back yet.
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(2));
        await playback.Recovery;

        Assert.Equal(3, _fixture.Engine.Loaded.Count);
        Assert.Equal(TimeSpan.FromSeconds(700), _fixture.Engine.Loaded[^1].Start);
        Assert.Equal(ServerPlaybackPhase.Reconnecting, playback.Status.Phase);
        Assert.All(_fixture.Silo.SentTo(HttpMethod.Post, ReplanPath),
            r => Assert.Contains("\"operation\":\"track_change\"", r.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEndAfterAFailure_ChangesNothing()
    {
        _fixture.Silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Problem(410, "playback_session_ended"));
        var playback = await _fixture.PlayingAsync();
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));
        await Until(() => playback.Status.Phase == ServerPlaybackPhase.Failed, "the session ends");

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "stream closed"));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(ServerPlaybackPhase.Failed, playback.Status.Phase);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ReplanPath));
    }

    [Fact]
    public async Task ARestart_StopsTheSessionItLeaves()
    {
        // After the outage the session refuses to go on (409): a new attempt, and the old session ends at 700 s.
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, ScriptedSilo.Problem(409, "conflict"));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(700));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await playback.Recovery;
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Delete, StopPath).Count == 1, "the old session is stopped");

        Assert.Equal(StreamUrl(OtherSession), _fixture.Engine.Loaded[^1].Url);
        Assert.Equal(700, _fixture.Body(HttpMethod.Delete, StopPath).GetProperty("position").GetDouble());
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
        await playback.Recovery;

        var replan = _fixture.Body(HttpMethod.Post, "api/v2/playback/" + OtherSession + "/replan");
        Assert.Equal("seek_reanchor", replan.GetProperty("operation").GetString());
        Assert.Equal(100, replan.GetProperty("position_seconds").GetDouble());
        Assert.Empty(_fixture.Engine.Seeks);
        Assert.Equal(2, _fixture.Engine.Loaded.Count);
    }

    [Fact]
    public async Task AfterAReanchor_TheViewerIsAtTheNewStreamsStart_InItsOwnTime()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.TranscodeDecision));
        _fixture.Silo.On(HttpMethod.Post, OtherReplanPath, Ok(StartedAgain));
        _fixture.Silo.On(HttpMethod.Delete, OtherStopPath, Ok(PlaybackPayloads.StopCompleted));
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(2));
        playback.Seek(100);
        await playback.Recovery;

        // Left before the new stream's first frame: 100 s, not the old stream's player time in the new plan's.
        await playback.StopAsync();

        Assert.Equal(100, _fixture.Body(HttpMethod.Delete, OtherStopPath).GetProperty("position").GetDouble());
    }

    [Fact]
    public async Task SeeksDuringAReanchor_WaitForItsStream()
    {
        var answer = new TaskCompletionSource();
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.TranscodeDecision));
        _fixture.Silo.On(HttpMethod.Post, OtherReplanPath, After(answer.Task, Ok(StartedAgain)));
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(2));

        playback.Seek(100);
        playback.Seek(200);
        answer.SetResult();
        await playback.Recovery;
        _fixture.Engine.RaiseState(Frame(100));

        // One replan of the session at a time (8.7); the later seek goes where the new stream can take it.
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Post, OtherReplanPath));
        Assert.Equal([TimeSpan.FromSeconds(200)], _fixture.Engine.Seeks);
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
