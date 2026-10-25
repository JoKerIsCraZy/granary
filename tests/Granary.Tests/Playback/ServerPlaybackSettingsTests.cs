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
    public async Task AnotherFile_StartsANewSessionAtThePosition_AndEndsTheOld()
    {
        // A second file after the one played last.
        string twoFiles = Regex.Replace(PlaybackPayloads.WatchStateOk, @"\}(\s*\],\s*""playback_variants"")",
            "}, { \"file_id\": \"43\", \"resolution\": \"2160p\", \"codec_video\": \"hevc\", \"codec_audio\": \"eac3\", \"hdr\": true, "
            + "\"container\": \"mkv\", \"file_size\": 2048, \"duration_seconds\": 10200, \"bitrate\": 16000000 }$1", RegexOptions.None);
        Assert.Contains("\"43\"", twoFiles, StringComparison.Ordinal);
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(twoFiles));
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
    public async Task TheSpeed_IsThisDevicesSetting_AndAChangeIsSavedForIt()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/settings/values/effective", Ok("""
            { "items": [ { "key": "player.playback_speed", "value": 1.25, "source": "profile_device" } ], "revision": 2 }
            """));
        _fixture.Silo.On(HttpMethod.Put, SpeedPath, ScriptedSilo.NoContent());
        var playback = await _fixture.PlayingAsync();
        Assert.Equal(1.25, playback.Speed);
        Assert.Equal(1.25, _fixture.Engine.Speed);

        playback.SetSpeed(2);
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Put, SpeedPath).Count == 1, "the speed is saved");

        Assert.Equal(2, _fixture.Engine.Speed);
        var saved = _fixture.Silo.SentTo(HttpMethod.Put, SpeedPath)[0];
        Assert.Equal("?scope=profile_device", saved.Uri.Query);
        Assert.Equal(2, _fixture.Body(HttpMethod.Put, SpeedPath).GetProperty("value").GetDouble());
    }
}
