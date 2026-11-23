using System.Text.Json;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>A start attests its decoders where the server takes software decoders, and keeps that for the attempt.</summary>
public sealed class AttestedDecodersStartTests
{
    private readonly ServerPlaybackFixture _fixture = new();

    private JsonElement Start(int index) => _fixture.Body(HttpMethod.Post, StartPath, index);

    private static string Evidence(JsonElement start) => start.GetProperty("client_capabilities").GetProperty("video_evidence").GetString()!;

    private static IEnumerable<string?> Features(JsonElement start) => start.GetProperty("client_features").EnumerateArray().Select(f => f.GetString());

    [Fact]
    public async Task AServerThatTakesSoftwareDecoders_GetsTheAttestedDecoders()
    {
        await _fixture.StartAsync(PlayRequest.Resume(MovieId));

        var start = Start(0);
        Assert.Equal(CapabilityEvidence.PlatformAttested, Evidence(start));
        Assert.Contains(PlaybackFeatures.SoftwareVideoDecodeV1, Features(start));
        Assert.NotEmpty(start.GetProperty("client_capabilities").GetProperty("video_decode").EnumerateArray());
    }

    [Fact]
    public async Task AServerWithoutSoftwareDecoders_GetsTheDeclaredReport()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/playback/capabilities", Ok(PlaybackPayloads.CapabilityAvailable.Replace(
            "\"software_video_decode_v1\",", "", StringComparison.Ordinal)));

        await _fixture.StartAsync(PlayRequest.Resume(MovieId));

        var start = Start(0);
        Assert.Equal(CapabilityEvidence.Declared, Evidence(start));
        Assert.DoesNotContain(PlaybackFeatures.SoftwareVideoDecodeV1, Features(start));
        Assert.False(start.GetProperty("client_capabilities").TryGetProperty("video_decode", out _));
    }

    [Fact]
    public async Task AFileSwitchedTo_KeepsTheAttestedDecoders()
    {
        // The movie with a second file, 43.
        string watch = System.Text.RegularExpressions.Regex.Replace(PlaybackPayloads.WatchStateOk, @"\}(\s*\],\s*""playback_variants"")",
            "}, { \"file_id\": \"43\", \"resolution\": \"2160p\", \"codec_video\": \"hevc\", \"codec_audio\": \"eac3\", \"hdr\": false, "
            + "\"container\": \"mkv\", \"file_size\": 2048, \"duration_seconds\": 10200, \"bitrate\": 16000000 }$1");
        _fixture.Silo.On(HttpMethod.Get, "api/v2/watch/" + MovieId, Ok(watch));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await _fixture.StartAsync(PlayRequest.Resume(MovieId, new PrePlayChoice("42", IsVersionPicked: true)));
        _fixture.Engine.RaiseState(Frame(600));

        playback.SelectVersion("43");
        await playback.Recovery;

        var start = Start(1);
        Assert.Equal("43", start.GetProperty("file_id").GetString());
        Assert.Equal(CapabilityEvidence.PlatformAttested, Evidence(start));
        Assert.Contains(PlaybackFeatures.SoftwareVideoDecodeV1, Features(start));
    }
}
