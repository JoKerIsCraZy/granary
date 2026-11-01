using System.Net;
using Granary.Core.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>
/// A plan change in flight (<see cref="ServerPlayback.PlanChange"/>): from the request for a new plan until the new stream
/// shows its first frame, the server refuses, or playback fails or stops. The web player's <c>replanning || replacing</c>.
/// </summary>
public sealed class ServerPlaybackPlanChangeTests
{
    private readonly ServerPlaybackFixture _fixture = new();
    private readonly List<PlanChange> _changes = [];

    private async Task<ServerPlayback> TranscodingAsync(string decision)
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(decision));
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(30));
        Follow(playback);
        return playback;
    }

    private async Task<ServerPlayback> PlayingAsync()
    {
        var playback = await _fixture.PlayingAsync();
        Follow(playback);
        return playback;
    }

    private void Follow(ServerPlayback playback) => playback.PlanChangeChanged += (_, _) => _changes.Add(playback.PlanChange);

    private static TaskCompletionSource<HttpResponseMessage> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task AQuality_IsAChange_FromThePickUntilTheNewStreamShowsAFrame()
    {
        var answer = Pending();
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => answer.Task);
        var playback = await TranscodingAsync(ServerPlaybackSettingsTests.Transcode("720p-medium"));

        playback.SelectQuality("1080p-high");
        Assert.Equal(PlanChange.Quality, playback.PlanChange);

        answer.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, ServerPlaybackSettingsTests.Transcode("1080p-high"))());
        await playback.Recovery;
        // The new stream is loading: the picture is not there yet.
        Assert.Equal(2, _fixture.Engine.Loaded.Count);
        Assert.Equal(PlanChange.Quality, playback.PlanChange);

        _fixture.Engine.RaiseState(Frame(2));
        Assert.Equal(PlanChange.None, playback.PlanChange);
        Assert.Equal([PlanChange.Quality, PlanChange.None], _changes);
    }

    [Fact]
    public async Task AQualityTheServerRefuses_EndsTheChange_AndTheStreamPlaysOn()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, ScriptedSilo.Problem(422, "validation_failed"));
        var playback = await TranscodingAsync(ServerPlaybackSettingsTests.Transcode("720p-medium"));

        playback.SelectQuality("1080p-high");
        await playback.Recovery;

        Assert.Equal(PlanChange.None, playback.PlanChange);
        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
        Assert.Equal([PlanChange.Quality, PlanChange.None], _changes);
    }

    [Fact]
    public async Task AQualityTheServerHasNoStreamFor_EndsTheChange()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.TerminalDecision));
        var playback = await TranscodingAsync(ServerPlaybackSettingsTests.Transcode("720p-medium"));

        playback.SelectQuality("1080p-high");
        await playback.Recovery;

        Assert.Equal(PlanChange.None, playback.PlanChange);
        Assert.Single(_fixture.Engine.Loaded);
    }

    [Fact]
    public async Task AnotherFile_IsAChange_UntilItPlays()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(ServerPlaybackSettingsTests.TwoFiles));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await PlayingAsync();
        _fixture.Engine.RaiseState(Frame(600));

        playback.SelectVersion("43");
        Assert.Equal(PlanChange.Version, playback.PlanChange);
        await playback.Recovery;
        Assert.Equal(PlanChange.Version, playback.PlanChange);

        _fixture.Engine.RaiseState(Frame(600));
        Assert.Equal(PlanChange.None, playback.PlanChange);
    }

    [Fact]
    public async Task AnotherFileTheServerDoesNotStart_EndsTheChange()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(ServerPlaybackSettingsTests.TwoFiles));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), ScriptedSilo.Problem(422, "validation_failed"));
        var playback = await PlayingAsync();

        playback.SelectVersion("43");
        await playback.Recovery;

        Assert.Equal(PlanChange.None, playback.PlanChange);
        Assert.Equal([PlanChange.Version, PlanChange.None], _changes);
    }

    [Fact]
    public async Task AnAudioTrackTheStreamLacks_IsAChange_UntilTheNewStreamPlays()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId,
            Ok(ServerPlaybackAudioTests.WithASecondAudioTrack(PlaybackPayloads.WatchStateOk)));
        _fixture.Silo.On(HttpMethod.Put, ServerPlaybackAudioTests.AudioPrefsPath, ScriptedSilo.NoContent());
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(ServerPlaybackAudioTests.TranscodeOfTheMovie(audio: 1)));
        var playback = await TranscodingAsync(ServerPlaybackAudioTests.TranscodeOfTheMovie(audio: 0));

        playback.SelectAudio(1);
        Assert.Equal(PlanChange.Audio, playback.PlanChange);
        await playback.Recovery;
        Assert.Equal(PlanChange.Audio, playback.PlanChange);

        _fixture.Engine.RaiseState(Frame(2));
        Assert.Equal(PlanChange.None, playback.PlanChange);
    }

    [Fact]
    public async Task AnAudioSwitchInThePlayer_IsNoChange()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId,
            Ok(ServerPlaybackAudioTests.WithASecondAudioTrack(PlaybackPayloads.WatchStateOk)));
        _fixture.Silo.On(HttpMethod.Put, ServerPlaybackAudioTests.AudioPrefsPath, ScriptedSilo.NoContent());
        var playback = await PlayingAsync();

        playback.SelectAudio(1);

        Assert.Equal([2], _fixture.Engine.AudioSwitches);
        Assert.Empty(_changes);
    }

    [Fact]
    public async Task LeavingABurnIn_IsAChange_UntilTheNewStreamPlays()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId,
            Ok(ServerPlaybackSubtitleTests.WithSubtitleTracks(PlaybackPayloads.WatchStateOk)));
        _fixture.Silo.On(HttpMethod.Put, ServerPlaybackSubtitleTests.SubtitlePrefsPath, ScriptedSilo.NoContent());
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(ServerPlaybackSubtitleTests.Showing(PlaybackPayloads.StartOpaqueIds, "burn_in", 3)));
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await PlayingAsync();

        playback.SelectSubtitle(null);
        Assert.Equal(PlanChange.Subtitles, playback.PlanChange);
        await playback.Recovery;

        _fixture.Engine.RaiseState(Frame(12.5));
        Assert.Equal([PlanChange.Subtitles, PlanChange.None], _changes);
    }

    [Fact]
    public async Task AnAddedSubtitle_IsAChange_UntilTheStreamThatListsItPlays()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId,
            Ok(ServerPlaybackSubtitleTests.WithSubtitleTracks(PlaybackPayloads.WatchStateOk)));
        _fixture.Silo.On(HttpMethod.Put, ServerPlaybackSubtitleTests.SubtitlePrefsPath, ScriptedSilo.NoContent());
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(ServerPlaybackSubtitleTests.ListingStored("\"sync_key\": \"stored-77\",")));
        var playback = await PlayingAsync();

        playback.ShowAddedSubtitle("77");
        Assert.Equal(PlanChange.Subtitles, playback.PlanChange);
        await playback.Recovery;
        Assert.Equal(PlanChange.Subtitles, playback.PlanChange);

        _fixture.Engine.RaiseState(Frame(30));
        Assert.Equal(PlanChange.None, playback.PlanChange);
    }

    [Fact]
    public async Task ASeekOutsideTheStream_IsAChange_UntilTheReanchoredStreamPlays()
    {
        _fixture.Silo.On(HttpMethod.Post, OtherReplanPath, Ok(StartedAgain));
        var playback = await TranscodingAsync(PlaybackPayloads.TranscodeDecision);

        playback.Seek(100);
        Assert.Equal(PlanChange.Seek, playback.PlanChange);
        await playback.Recovery;
        Assert.Equal(PlanChange.Seek, playback.PlanChange);

        _fixture.Engine.RaiseState(Frame(100));
        Assert.Equal(PlanChange.None, playback.PlanChange);
    }

    [Fact]
    public async Task ASeekWithinTheStream_IsNoChange()
    {
        var playback = await TranscodingAsync(PlaybackPayloads.TranscodeDecision);

        playback.Seek(599);

        Assert.Empty(_changes);
    }

    [Fact]
    public async Task ASessionTheServerForgot_IsAChange_UntilTheNewAttemptPlays()
    {
        _fixture.Silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Problem(404, "not_found"));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await PlayingAsync();
        _fixture.Engine.RaiseState(Frame(300));

        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));
        await Until(() => _fixture.Engine.Loaded.Count == 2, "the new attempt loads");
        Assert.Equal(PlanChange.Restart, playback.PlanChange);

        _fixture.Engine.RaiseState(Frame(300));
        Assert.Equal(PlanChange.None, playback.PlanChange);
    }

    [Fact]
    public async Task ANewStreamThatBreaksOffBeforeItsFirstFrame_KeepsTheChange_UntilAStreamPlays()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(ServerPlaybackSettingsTests.Transcode("1080p-high")));
        var playback = await TranscodingAsync(ServerPlaybackSettingsTests.Transcode("720p-medium"));
        playback.SelectQuality("1080p-high");
        await playback.Recovery;

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        Assert.Equal(PlanChange.Quality, playback.PlanChange);
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await playback.Recovery;
        Assert.Equal(3, _fixture.Engine.Loaded.Count);
        Assert.Equal(PlanChange.Quality, playback.PlanChange);

        _fixture.Engine.RaiseState(Frame(2));
        Assert.Equal(PlanChange.None, playback.PlanChange);
        Assert.Equal([PlanChange.Quality, PlanChange.None], _changes);
    }

    [Fact]
    public async Task AFailureDuringAChange_EndsIt()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, ScriptedSilo.Problem(410, "playback_session_ended"));
        var playback = await TranscodingAsync(ServerPlaybackSettingsTests.Transcode("720p-medium"));

        playback.SelectQuality("1080p-high");
        await playback.Recovery;

        Assert.Equal(ServerPlaybackPhase.Failed, playback.Status.Phase);
        Assert.Equal(PlanChange.None, playback.PlanChange);
    }

    [Fact]
    public async Task TheStopDuringAChange_EndsIt_AndALateAnswerChangesNothing()
    {
        var answer = Pending();
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => answer.Task);
        var playback = await TranscodingAsync(ServerPlaybackSettingsTests.Transcode("720p-medium"));
        playback.SelectQuality("1080p-high");

        var stop = playback.StopAsync();
        Assert.Equal(PlanChange.None, playback.PlanChange);
        answer.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, ServerPlaybackSettingsTests.Transcode("1080p-high"))());
        await playback.Recovery;
        await stop;

        Assert.Equal([PlanChange.Quality, PlanChange.None], _changes);
        Assert.Single(_fixture.Engine.Loaded);
    }

    [Fact]
    public async Task APickMadeDuringAChange_FollowsIt_WithoutTheChangeEndingBetween()
    {
        var first = Pending();
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => first.Task, Ok(ServerPlaybackSettingsTests.Transcode("720p-medium")));
        var playback = await TranscodingAsync(ServerPlaybackSettingsTests.Transcode("720p-medium"));

        playback.SelectQuality("1080p-high");
        playback.SelectQuality("auto");
        first.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, ServerPlaybackSettingsTests.Transcode("1080p-high"))());
        await playback.Recovery;
        _fixture.Engine.RaiseState(Frame(2));
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Post, ReplanPath).Count == 2, "the later pick is asked for");
        await playback.Recovery;
        Assert.Equal(PlanChange.Quality, playback.PlanChange);

        _fixture.Engine.RaiseState(Frame(2));
        Assert.Equal([PlanChange.Quality, PlanChange.None], _changes);
    }

    [Fact]
    public async Task ASubtitleAddedDuringAQualityChange_GoesOnUnderTheQualityChange()
    {
        var answer = Pending();
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId,
            Ok(ServerPlaybackSubtitleTests.WithSubtitleTracks(PlaybackPayloads.WatchStateOk)));
        _fixture.Silo.On(HttpMethod.Put, ServerPlaybackSubtitleTests.SubtitlePrefsPath, ScriptedSilo.NoContent());
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => answer.Task,
            Ok(ServerPlaybackSubtitleTests.ListingStored("\"sync_key\": \"stored-77\",")));
        var playback = await PlayingAsync();

        playback.SelectQuality("1080p-high");
        playback.ShowAddedSubtitle("77");
        answer.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.StartOpaqueIds)());
        await playback.Recovery;

        // The plan that lists the subtitle is asked for while the quality's stream still loads: one change, the viewer's.
        Assert.Equal("track_change", _fixture.Body(HttpMethod.Post, ReplanPath, 1).GetProperty("operation").GetString());
        Assert.Equal(PlanChange.Quality, playback.PlanChange);
        _fixture.Engine.RaiseState(Frame(30));
        Assert.Equal([PlanChange.Quality, PlanChange.None], _changes);
    }

    // Off the usual paths

    [Fact]
    public async Task AnUnexpectedFailureDuringAChange_EndsIt_WithTheFailure()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => Task.FromException<HttpResponseMessage>(new InvalidOperationException("not a request failure")));
        var playback = await TranscodingAsync(ServerPlaybackSettingsTests.Transcode("720p-medium"));

        playback.SelectQuality("1080p-high");
        await playback.Recovery;

        Assert.Equal(ServerPlaybackPhase.Failed, playback.Status.Phase);
        Assert.Equal(PlanChange.None, playback.PlanChange);
        Assert.Equal([PlanChange.Quality, PlanChange.None], _changes);
    }

    [Fact]
    public async Task MediaHeadersRefusedForTheNewStream_EndTheChange_WithTheFailure()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(ServerPlaybackSettingsTests.Transcode("1080p-high")));
        var playback = await TranscodingAsync(ServerPlaybackSettingsTests.Transcode("720p-medium"));
        _fixture.Auth.FailWith = new MediaAuthorizationException(MediaAuthorizationFailure.SignedOut, "Sign in to play from this server.");

        playback.SelectQuality("1080p-high");
        await playback.Recovery;

        Assert.Equal(ServerPlaybackPhase.Failed, playback.Status.Phase);
        Assert.Single(_fixture.Engine.Loaded);
        Assert.Equal([PlanChange.Quality, PlanChange.None], _changes);
    }

    [Fact]
    public async Task ANewStreamThatNeverShowsAPicture_KeepsTheChange_UntilPlaybackGivesUp()
    {
        // The transcode is another plan than the direct play: when it shows nothing, it is replanned as a failure.
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.TranscodeDecision.Replace(OtherSession, Session, StringComparison.Ordinal)));
        var playback = await PlayingAsync();
        playback.SelectQuality("1080p-high");
        await playback.Recovery;

        for (int load = 0; load < 20 && playback.Status.Phase != ServerPlaybackPhase.Failed; load++)
        {
            Assert.Equal(PlanChange.Quality, playback.PlanChange);
            await _fixture.AdvanceAsync(TimeSpan.FromSeconds(90));
            await playback.Recovery;
        }

        Assert.Equal(ServerPlaybackPhase.Failed, playback.Status.Phase);
        Assert.True(_fixture.Engine.Loaded.Count > 3);
        Assert.Equal([PlanChange.Quality, PlanChange.None], _changes);
    }

    [Fact]
    public async Task FramesOfTheOldStream_DoNotEndTheChange()
    {
        var answer = Pending();
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => answer.Task);
        var playback = await TranscodingAsync(ServerPlaybackSettingsTests.Transcode("720p-medium"));

        playback.SelectQuality("1080p-high");
        _fixture.Engine.RaiseState(Frame(31));
        Assert.Equal(PlanChange.Quality, playback.PlanChange);
        answer.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, ServerPlaybackSettingsTests.Transcode("1080p-high"))());
        await playback.Recovery;
        // A state of the first load, reported before the new load went out and arriving after it.
        _fixture.Engine.RaiseState(Frame(32) with { LoadId = 1 });
        Assert.Equal(PlanChange.Quality, playback.PlanChange);

        _fixture.Engine.RaiseState(Frame(2));
        Assert.Equal([PlanChange.Quality, PlanChange.None], _changes);
    }

    [Fact]
    public async Task ASeekOutsideTheStreamDuringAQualityChange_FollowsIt_AsASeek()
    {
        var answer = Pending();
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => answer.Task, Ok(ServerPlaybackSettingsTests.Transcode("1080p-high")));
        var playback = await TranscodingAsync(ServerPlaybackSettingsTests.Transcode("720p-medium"));

        playback.SelectQuality("1080p-high");
        playback.Seek(100);
        answer.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, ServerPlaybackSettingsTests.Transcode("1080p-high"))());
        await playback.Recovery;
        // The quality's stream shows: the seek that waited for it goes outside it, and the server re-anchors.
        _fixture.Engine.RaiseState(Frame(2));
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Post, ReplanPath).Count == 2, "the seek is replanned");
        await playback.Recovery;

        Assert.Equal("seek_reanchor", _fixture.Body(HttpMethod.Post, ReplanPath, 1).GetProperty("operation").GetString());
        Assert.Equal(PlanChange.Seek, playback.PlanChange);
        _fixture.Engine.RaiseState(Frame(2));
        Assert.Equal([PlanChange.Quality, PlanChange.Seek, PlanChange.None], _changes);
    }

    [Fact]
    public async Task AReconnect_IsNoPlanChange()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await PlayingAsync();
        _fixture.Engine.RaiseState(Frame(700));

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await playback.Recovery;
        _fixture.Engine.RaiseState(Frame(700));

        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
        Assert.Empty(_changes);
    }

    [Fact]
    public async Task TheStart_AndItsRecovery_AreNoPlanChange()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.TranscodeDecision.Replace(OtherSession, Session, StringComparison.Ordinal)));
        var playback = _fixture.Create();
        Follow(playback);
        await playback.StartAsync(PlayRequest.Resume(MovieId), TestContext.Current.CancellationToken);

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "loading failed"));
        await playback.Recovery;
        _fixture.Engine.RaiseState(Frame(2));

        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
        Assert.Empty(_changes);
    }
}
