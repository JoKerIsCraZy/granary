using System.Net;
using System.Text.RegularExpressions;
using Granary.Core.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>The settings menu's quality, file and speed (docs/api-contract.md, 8.7 and 8.11).</summary>
public sealed class ServerPlaybackSettingsTests
{
    private const string SpeedPath = "api/v2/settings/values/player.playback_speed";

    private readonly ServerPlaybackFixture _fixture = new();

    // The transcode of the movie's file with its two rungs, as a plan of the first session.
    private static string Transcode(string active) => PlaybackPayloads.TranscodeDecision
        .Replace(OtherSession, Session, StringComparison.Ordinal)
        .Replace("\"effective_media_file_id\": \"10\"", "\"effective_media_file_id\": \"42\"", StringComparison.Ordinal)
        .Replace("\"active_quality\": \"720p-medium\"", $"\"active_quality\": \"{active}\"", StringComparison.Ordinal);

    [Fact]
    public async Task TheMenu_IsThePlansRungs_WithTheOneItDelivers()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(Transcode("720p-medium")));
        var playback = await _fixture.StartAsync();

        Assert.Equal(["1080p-high", "720p-medium"], playback.Qualities.Select(q => q.Label));
        Assert.Equal("720p-medium", playback.ActiveQuality);
        Assert.Equal("auto", playback.QualityPreference);
    }

    [Fact]
    public async Task APick_AsksForAPlanOfThatQuality_FromWhereTheViewerIs()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(Transcode("720p-medium")));
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, Ok(Transcode("1080p-high")));
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(30));

        playback.SelectQuality("1080p-high");
        await Until(() => playback.ActiveQuality == "1080p-high", "the new plan is taken");

        var replan = _fixture.Body(HttpMethod.Post, ReplanPath);
        Assert.Equal("quality_change", replan.GetProperty("operation").GetString());
        Assert.Equal("1080p-high", replan.GetProperty("quality_preference").GetString());
        Assert.False(replan.TryGetProperty("failure", out _));
        Assert.Equal("1080p-high", playback.QualityPreference);
        Assert.Equal(2, _fixture.Engine.Loaded.Count);
    }

    [Fact]
    public async Task AQualityTheServerRefuses_PlaysOn_AndCanBePickedAgain()
    {
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(Transcode("720p-medium")));
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, ScriptedSilo.Problem(422, "validation_failed"), Ok(Transcode("1080p-high")));
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(30));

        playback.SelectQuality("1080p-high");
        await playback.Recovery;

        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
        Assert.Equal(0, _fixture.Engine.Stops);
        Assert.Equal("auto", playback.QualityPreference);

        playback.SelectQuality("1080p-high");
        await Until(() => playback.ActiveQuality == "1080p-high", "the pick made again is taken");
        Assert.Equal("1080p-high", _fixture.Body(HttpMethod.Post, ReplanPath, 1).GetProperty("quality_preference").GetString());
    }

    [Fact]
    public async Task ASeekWhileTheServerRefusesAQuality_GoesToTheStreamThatPlaysOn()
    {
        var refusal = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(Transcode("720p-medium")));
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => refusal.Task);
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(30));

        playback.SelectQuality("1080p-high");
        // Within what the stream holds (it starts at 598 s, and the viewer is at 628 s).
        playback.Seek(620);
        Assert.Empty(_fixture.Engine.Seeks);
        refusal.SetResult(await ScriptedSilo.Problem(422, "validation_failed")());
        await playback.Recovery;

        Assert.Equal([TimeSpan.FromSeconds(22)], _fixture.Engine.Seeks);
        Assert.Single(_fixture.Engine.Loaded);
    }

    [Fact]
    public async Task AStreamThatBreaksOffWhileTheServerRefusesAQuality_IsReconnected()
    {
        var refusal = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(Transcode("720p-medium")));
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => refusal.Task);
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(30));

        playback.SelectQuality("1080p-high");
        _fixture.Engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "connection reset"));
        refusal.SetResult(await ScriptedSilo.Problem(422, "validation_failed")());
        await Until(() => playback.Status.Phase == ServerPlaybackPhase.Reconnecting, "the playback reconnects");
    }

    [Fact]
    public async Task AQualityPickedWhileTheServerPlansAnother_IsAskedForOnceThatPlays()
    {
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(Transcode("720p-medium")));
        _fixture.Silo.On(HttpMethod.Post, ReplanPath, () => first.Task, Ok(Transcode("720p-medium")));
        var playback = await _fixture.StartAsync();
        _fixture.Engine.RaiseState(Frame(30));

        playback.SelectQuality("1080p-high");
        playback.SelectQuality("auto");
        Assert.Equal("auto", playback.QualityPreference);
        first.SetResult(await ScriptedSilo.Json(HttpStatusCode.OK, Transcode("1080p-high"))());
        await playback.Recovery;
        _fixture.Engine.RaiseState(Frame(2));
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Post, ReplanPath).Count == 2, "the later pick is asked for");

        Assert.Equal("auto", _fixture.Body(HttpMethod.Post, ReplanPath, 1).GetProperty("quality_preference").GetString());
    }

    [Fact]
    public async Task AnotherFile_StartsANewSessionAtThePosition_AndEndsTheOld()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(TwoFiles));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        _fixture.Silo.On(HttpMethod.Delete, StopPath, Ok(PlaybackPayloads.StopCompleted));
        var playback = await _fixture.PlayingAsync();
        Assert.Equal(["42", "43"], playback.Versions.Select(v => v.FileId));
        _fixture.Engine.RaiseState(Frame(600));

        playback.SelectVersion("43");
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Post, StartPath).Count == 2, "the file is started");

        var start = _fixture.Body(HttpMethod.Post, StartPath, 1);
        Assert.Equal("43", start.GetProperty("file_id").GetString());
        Assert.Equal(600, start.GetProperty("start_position").GetDouble(), 1);
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Delete, StopPath).Count == 1, "the old session ends");
    }

    [Fact]
    public async Task AFileStartedAfterTheServerWasReinstalled_IsStartedAgainWithItsNewInstallation()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(TwoFiles));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), ScriptedSilo.Problem(409, "installation_changed"),
            Created(StartedAgain));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(600));
        int capabilityReads = _fixture.Silo.SentTo(HttpMethod.Get, "api/v2/playback/capabilities").Count;

        playback.SelectVersion("43");
        await playback.Recovery;

        Assert.Equal(capabilityReads + 1, _fixture.Silo.SentTo(HttpMethod.Get, "api/v2/playback/capabilities").Count);
        Assert.Equal("43", _fixture.Body(HttpMethod.Post, StartPath, 2).GetProperty("file_id").GetString());
        Assert.Equal(2, _fixture.Engine.Loaded.Count);
    }

    [Fact]
    public async Task AFileTheServerDoesNotStart_LeavesTheOneThatPlays_ForLaterAttemptsToo()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(TwoFiles));
        _fixture.Silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Problem(404, "not_found"));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), ScriptedSilo.Problem(422, "validation_failed"),
            Created(StartedAgain));
        var playback = await _fixture.PlayingAsync();
        _fixture.Engine.RaiseState(Frame(600));

        playback.SelectVersion("43");
        await playback.Recovery;

        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
        Assert.Equal(0, _fixture.Engine.Stops);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Delete, StopPath));
        Assert.Equal("42", playback.RequestedFileId);

        // The session is lost: the new attempt asks for the file that played, not the one refused.
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Post, StartPath).Count == 3, "a new attempt starts");
        Assert.Equal("42", _fixture.Body(HttpMethod.Post, StartPath, 2).GetProperty("file_id").GetString());
    }

    [Fact]
    public async Task TheSpeed_IsThisDevicesSetting_AndAChangeIsSavedForIt()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/settings/values/effective", Ok("""
            { "items": [ { "key": "player.playback_speed", "value": 1.25, "source": "profile_device" } ], "revision": 2 }
            """));
        _fixture.Silo.On(HttpMethod.Put, SpeedPath, ScriptedSilo.NoContent());
        var playback = await _fixture.PlayingAsync();
        Assert.Equal(1.25, playback.Speed);
        Assert.Equal(1.25, _fixture.Engine.Speed);

        playback.SetSpeed(1.5);
        playback.SetSpeed(1.75);
        playback.SetSpeed(2);
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Put, SpeedPath).Count == 1, "the speed is saved once it settles");

        Assert.Equal(2, _fixture.Engine.Speed);
        var saved = _fixture.Silo.SentTo(HttpMethod.Put, SpeedPath)[0];
        Assert.Equal("?scope=profile_device", saved.Uri.Query);
        Assert.Equal(2, _fixture.Body(HttpMethod.Put, SpeedPath).GetProperty("value").GetDouble());
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Put, SpeedPath));
    }

    [Fact]
    public async Task ASpeedSetJustBeforeTheStop_IsSavedWithIt()
    {
        _fixture.Silo.On(HttpMethod.Put, SpeedPath, ScriptedSilo.NoContent());
        _fixture.Silo.On(HttpMethod.Delete, StopPath, Ok(PlaybackPayloads.StopCompleted));
        var playback = await _fixture.PlayingAsync();

        playback.SetSpeed(1.5);
        await playback.StopAsync();

        Assert.Equal(1.5, _fixture.Body(HttpMethod.Put, SpeedPath).GetProperty("value").GetDouble());
    }

    // A second file after the one played last.
    private static string TwoFiles
    {
        get
        {
            string twoFiles = Regex.Replace(PlaybackPayloads.WatchStateOk, @"\}(\s*\],\s*""playback_variants"")",
                "}, { \"file_id\": \"43\", \"resolution\": \"2160p\", \"codec_video\": \"hevc\", \"codec_audio\": \"eac3\", \"hdr\": true, "
                + "\"container\": \"mkv\", \"file_size\": 2048, \"duration_seconds\": 10200, \"bitrate\": 16000000 }$1", RegexOptions.None);
            Assert.Contains("\"43\"", twoFiles, StringComparison.Ordinal);
            return twoFiles;
        }
    }
}
