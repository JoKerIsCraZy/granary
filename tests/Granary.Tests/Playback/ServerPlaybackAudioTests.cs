using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Granary.Core.Api;
using Granary.Core.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>The viewer's audio pick during playback (docs/api-contract.md, 8.8), as Silo's Apple and Android clients make it.</summary>
public sealed class ServerPlaybackAudioTests
{
    private readonly ServerPlaybackFixture _fixture = new();

    // The movie's picks are kept under its content id.
    private static readonly string AudioPrefsPath = "api/v2/audio-prefs/" + SiloHttp.PathSegment("movie:heat-1995", "key");

    public ServerPlaybackAudioTests()
    {
        // English, the file's default, then German.
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(WithASecondAudioTrack(PlaybackPayloads.WatchStateOk)));
        _fixture.Silo.On(HttpMethod.Put, AudioPrefsPath, ScriptedSilo.NoContent());
    }

    // The transcode of the movie's file, as the answer to a replan of the first session.
    private static string TranscodeOfTheMovie(int audio = 1) => PlaybackPayloads.TranscodeDecision
        .Replace(OtherSession, Session, StringComparison.Ordinal)
        .Replace("\"effective_media_file_id\": \"10\"", "\"effective_media_file_id\": \"42\"", StringComparison.Ordinal)
        .Replace("\"audio\": { \"id\": \"file:9:audio:1\" }", $"\"audio\": {{ \"id\": \"file:42:audio:{audio}\", \"index\": {audio} }}", StringComparison.Ordinal);

    // The first "audio" object of a decision is selected_tracks.audio; the claims have one too.
    private static string SelectingAudio(string decision, int ordinal)
    {
        string audio = $"{{ \"id\": \"file:42:audio:{ordinal}\", \"index\": {ordinal} }}";
        string selecting = new Regex(@"(""audio"":\s*)\{[^}]*\}").Replace(decision, "$1" + audio, 1);
        Assert.Contains(audio, selecting, StringComparison.Ordinal);
        return selecting;
    }

    private static string WithASecondAudioTrack(string watch)
    {
        string twoTracks = new Regex(@"(""audio_tracks"":\s*\[\s*\{[^}]*\})")
            .Replace(watch, "$1, { \"language\": \"ger\", \"title\": \"Deutsch\", \"codec\": \"eac3\", \"layout\": \"5.1(side)\", \"channels\": 6, \"default\": false }", 1);
        Assert.Contains("\"ger\"", twoTracks, StringComparison.Ordinal);
        return twoTracks;
    }

    private async Task<ServerPlayback> TranscodingAsync()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(TranscodeOfTheMovie(audio: 0)));
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(2));
        return playback;
    }

    [Fact]
    public async Task TheMenu_ListsTheFilesTracks_WithTheOneThatPlays()
    {
        var playback = await _fixture.PlayingAsync();

        Assert.Equal(["eng", "ger"], playback.AudioTracks.Select(t => t.Language));
        Assert.Equal(0, playback.AudioTrack);
    }

    [Fact]
    public async Task ADirectPlay_SwitchesInThePlayer_WithoutAskingTheServer()
    {
        var playback = await _fixture.PlayingAsync();
        int changes = 0;
        playback.TracksChanged += (_, _) => changes++;

        playback.SelectAudio(1);

        Assert.Equal([2], _fixture.Engine.AudioSwitches);
        Assert.Equal(1, playback.AudioTrack);
        Assert.Equal(1, changes);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Post, ReplanPath));
        Assert.Single(_fixture.Engine.Loaded);
    }

    [Fact]
    public async Task APick_IsSavedForTheTitle_WithWhatRecognizesTheTrackElsewhere()
    {
        var playback = await _fixture.PlayingAsync();

        playback.SelectAudio(1);
        await playback.StopAsync();

        var body = _fixture.Body(HttpMethod.Put, AudioPrefsPath);
        Assert.Equal(1, body.GetProperty("audio_track_index").GetInt32());
        Assert.Equal("ger", body.GetProperty("audio_language").GetString());
        var signature = body.GetProperty("track_signature");
        Assert.Equal("""{"language":"ger","title":"Deutsch","codec":"eac3","layout":"5.1(side)","channels":6}""", signature.GetRawText());
    }

    [Fact]
    public async Task WhatThePlanChose_IsNotSaved()
    {
        var playback = await _fixture.PlayingAsync();

        playback.SelectAudio(0);
        await playback.StopAsync();

        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Put, AudioPrefsPath));
        Assert.Empty(_fixture.Engine.AudioSwitches);
    }

    [Fact]
    public async Task AFailedSave_LeavesPlaybackAlone()
    {
        _fixture.Silo.On(HttpMethod.Put, AudioPrefsPath, ScriptedSilo.Problem(422, "validation_failed"));
        var playback = await _fixture.PlayingAsync();

        playback.SelectAudio(1);
        await playback.StopAsync();

        Assert.Equal(1, playback.AudioTrack);
        Assert.NotEqual(ServerPlaybackPhase.Failed, playback.Status.Phase);
    }

    [Fact]
    public async Task ASwitchThePlayerRefuses_AsksForAStreamWithTheTrack()
    {
        _fixture.Engine.AudioSwitchTakes = false;
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(SelectingAudio(PlaybackPayloads.StartOpaqueIds, 1)));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(700));

        playback.SelectAudio(1);
        await playback.Recovery;

        var replan = _fixture.Body(HttpMethod.Post, ReplanPath);
        Assert.Equal("track_change", replan.GetProperty("operation").GetString());
        Assert.Equal("file:42:audio:1", replan.GetProperty("selected_tracks").GetProperty("audio").GetProperty("id").GetString());
        Assert.Equal(700, replan.GetProperty("position_seconds").GetDouble());
        Assert.False(replan.TryGetProperty("failure", out _));
        var reload = _fixture.Engine.Loaded[^1];
        Assert.Equal(2, reload.AudioStream);
        Assert.Equal(TimeSpan.FromSeconds(700), reload.Start);
    }

    [Fact]
    public async Task AStreamTheServerMade_IsReplannedWithTheTrack()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(TranscodeOfTheMovie(audio: 1)));
        var playback = await TranscodingAsync();
        Assert.Equal(0, playback.AudioTrack);

        playback.SelectAudio(1);
        await playback.Recovery;

        Assert.Empty(_fixture.Engine.AudioSwitches);
        var replan = _fixture.Body(HttpMethod.Post, ReplanPath);
        Assert.Equal("track_change", replan.GetProperty("operation").GetString());
        var audio = replan.GetProperty("selected_tracks").GetProperty("audio");
        Assert.Equal(("file:42:audio:1", 1), (audio.GetProperty("id").GetString(), audio.GetProperty("index").GetInt32()));
        Assert.Equal(2, _fixture.Engine.Loaded.Count);
        Assert.Equal(1, playback.AudioTrack);
    }

    [Fact]
    public async Task APickBack_WhileTheServerPlansTheFirst_IsPlayedOnceThatArrives()
    {
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => first.Task, Ok(TranscodeOfTheMovie(audio: 0)));
        var playback = await TranscodingAsync();

        playback.SelectAudio(1);
        playback.SelectAudio(0);
        first.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, TranscodeOfTheMovie(audio: 1))());
        await playback.Recovery;
        _fixture.Engine.RaiseState(Frame(2));
        await playback.Recovery;

        var second = _fixture.Body(HttpMethod.Post, ReplanPath, 1);
        Assert.Equal("file:42:audio:0", second.GetProperty("selected_tracks").GetProperty("audio").GetProperty("id").GetString());
        Assert.Equal(0, playback.AudioTrack);
        Assert.Equal([1, 0], _fixture.Silo.SentTo(HttpMethod.Put, AudioPrefsPath)
            .Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("audio_track_index").GetInt32()));
    }

    [Fact]
    public async Task ATrackChangeTheServerRefuses_LeavesTheStreamPlaying()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, ScriptedSilo.Problem(422, "validation_failed"));
        var playback = await TranscodingAsync();

        playback.SelectAudio(1);
        await playback.Recovery;

        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
        Assert.Single(_fixture.Engine.Loaded);
        Assert.Equal(0, _fixture.Engine.Stops);
        Assert.Equal(0, playback.AudioTrack);
    }

    [Fact]
    public async Task APickWhileTheStreamIsReplaced_AppliesToTheNextOne()
    {
        var replan = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => replan.Task);
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));

        playback.SelectAudio(1);
        Assert.Empty(_fixture.Engine.AudioSwitches);

        replan.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, PlaybackPayloads.StartOpaqueIds)());
        await playback.Recovery;
        _fixture.Engine.RaiseState(Frame(12.5));

        Assert.Equal([2], _fixture.Engine.AudioSwitches);
        Assert.Equal(1, playback.AudioTrack);
    }

    [Fact]
    public async Task APickSwitchedInThePlayer_StaysAcrossAReload()
    {
        var playback = await _fixture.PlayingAsync();
        playback.SelectAudio(1);

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.AuthenticationFailed, null));
        await playback.Recovery;

        Assert.Equal(2, _fixture.Engine.Loaded[^1].AudioStream);
    }

    [Fact]
    public async Task APickSwitchedInThePlayer_GoesWithTheNextReplan()
    {
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(PlaybackPayloads.StartOpaqueIds));
        var playback = await _fixture.PlayingAsync();
        playback.SelectAudio(1);

        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await playback.Recovery;

        var replan = _fixture.Body(HttpMethod.Post, ReplanPath);
        Assert.Equal("file:42:audio:1", replan.GetProperty("selected_tracks").GetProperty("audio").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ANewAttempt_StartsWithThePick()
    {
        _fixture.Silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Problem(404, "not_found"));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await _fixture.PlayingAsync();
        playback.SelectAudio(1);

        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, _fixture.Body(HttpMethod.Post, StartPath, 1).GetProperty("audio_track_index").GetInt32());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task ATrackTheFileDoesNotHave_IsIgnored(int ordinal)
    {
        var playback = await _fixture.PlayingAsync();

        playback.SelectAudio(ordinal);

        Assert.Empty(_fixture.Engine.AudioSwitches);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Put, AudioPrefsPath));
    }

    [Fact]
    public async Task AfterTheStop_APickChangesNothing()
    {
        var playback = await _fixture.PlayingAsync();
        await playback.StopAsync();

        playback.SelectAudio(1);

        Assert.Empty(_fixture.Engine.AudioSwitches);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Put, AudioPrefsPath));
    }
}
