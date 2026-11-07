using System.Text.Json;
using System.Text.RegularExpressions;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>
/// Whether a start claims to tone-map HDR itself follows the codecs of the files the server may send for it: the file
/// started, and the title's other files unless the start keeps to it (docs/api-contract.md, 13.2).
/// </summary>
public sealed class HdrClaimTests
{
    private const string WatchPath = "api/v2/watch/" + MovieId;

    // File 42, picked on the detail page: the server may send no other.
    private static readonly PrePlayChoice PickedHevc = new("42", IsVersionPicked: true);

    private readonly ServerPlaybackFixture _fixture = new();

    public HdrClaimTests()
    {
        // A GPU that decodes HEVC at 10 bits and VP9 only at 8, as several common cards report.
        _fixture.Device.Facts = _fixture.Device.Facts with
        {
            HardwareVideoCodecs = ["h264", "hevc", "vp9"],
            HardwareTenBitVideoCodecs = ["hevc"],
            SoftwareVideoCodecs = ["h264", "mpeg2video"],
        };
    }

    // The movie as file 42 in the first codec and, with a second codec, a 4K HDR file 43 in that one.
    private static string Title(string codec, string? otherCodec = null)
    {
        string watch = PlaybackPayloads.WatchStateOk.Replace("\"codec_video\": \"h264\"", $"\"codec_video\": \"{codec}\"", StringComparison.Ordinal);
        if (otherCodec is null)
        {
            return watch;
        }
        string twoFiles = Regex.Replace(watch, @"\}(\s*\],\s*""playback_variants"")",
            "}, { \"file_id\": \"43\", \"resolution\": \"2160p\", \"codec_video\": \"" + otherCodec + "\", \"codec_audio\": \"eac3\", \"hdr\": true, "
            + "\"container\": \"mkv\", \"file_size\": 2048, \"duration_seconds\": 10200, \"bitrate\": 16000000 }$1", RegexOptions.None);
        Assert.Contains("\"43\"", twoFiles, StringComparison.Ordinal);
        return twoFiles;
    }

    private bool StartMapsHdr(int index = 0) => MapsHdr(_fixture.Body(HttpMethod.Post, StartPath, index));

    private static bool MapsHdr(JsonElement body) =>
        body.GetProperty("client_playback_context").GetProperty("deliveries").GetProperty("original_http").GetProperty("validated_claims")
            .EnumerateArray().Any(c => c.GetString() == DeliveryClaims.ClientManagedDynamicRangeV1);

    [Fact]
    public async Task AnHevcTitle_ClaimsToneMapping_ThoughTheVp9DecoderIsEightBitOnly()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc")));

        await _fixture.StartAsync();

        Assert.True(StartMapsHdr());
        Assert.Contains(_fixture.Logs.Lines, line => line.Contains("HDR originals of this title are played directly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AVp9Title_LeavesHdrToTheServer_WhenTheVp9DecoderIsEightBitOnly()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("vp9")));

        await _fixture.StartAsync();

        Assert.False(StartMapsHdr());
        Assert.Contains(_fixture.Logs.Lines, line => line.Contains("HDR originals of this title are tone-mapped by the server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATitleWithAVp9VersionTheServerMayPick_LeavesHdrToTheServer()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));

        await _fixture.StartAsync();

        var start = _fixture.Body(HttpMethod.Post, StartPath);
        Assert.Equal("42", start.GetProperty("file_id").GetString());
        Assert.False(start.TryGetProperty("allow_alternate_versions", out _));
        Assert.False(MapsHdr(start));
    }

    [Fact]
    public async Task APickedHevcVersion_ClaimsToneMapping_ThoughTheTitleHasAVp9Version()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));

        await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedHevc));

        var start = _fixture.Body(HttpMethod.Post, StartPath);
        Assert.False(start.GetProperty("allow_alternate_versions").GetBoolean());
        Assert.True(MapsHdr(start));
    }

    [Fact]
    public async Task ARoomsHevcFile_ClaimsToneMapping_ThoughTheTitleHasAVp9Version()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));

        await _fixture.StartAsync(PlayRequest.WatchParty(MovieId, "42", selectionRevision: 1));

        Assert.True(StartMapsHdr());
    }

    [Fact]
    public async Task AFileWithoutAKnownCodec_ChecksEveryHdrCodecTheReportLists()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("")));

        await _fixture.StartAsync();

        Assert.False(StartMapsHdr());
    }

    [Fact]
    public async Task SoftwareDecoding_ClaimsToneMappingForAVp9Title()
    {
        _fixture.DevicePreferences.DecodesInSoftware = true;
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("vp9")));

        await _fixture.StartAsync();

        Assert.True(StartMapsHdr());
    }

    [Fact]
    public async Task AFileSwitchedToInThePlayer_StartsWithTheClaimForThatFile()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedHevc));
        _fixture.Engine.RaiseState(Frame(600));
        Assert.True(StartMapsHdr());

        playback.SelectVersion("43");
        await playback.Recovery;

        var start = _fixture.Body(HttpMethod.Post, StartPath, 1);
        Assert.Equal("43", start.GetProperty("file_id").GetString());
        Assert.False(MapsHdr(start));
        Assert.Contains(_fixture.Logs.Lines, line => line.Contains("HDR originals of this title are tone-mapped by the server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileSwitchedTo_KeepsTheTitlesSoftwareDecodingChoice()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedHevc));
        _fixture.Engine.RaiseState(Frame(600));

        // Settings → Playback says it counts from the next title.
        _fixture.DevicePreferences.DecodesInSoftware = true;
        playback.SelectVersion("43");
        await playback.Recovery;

        var start = _fixture.Body(HttpMethod.Post, StartPath, 1);
        Assert.False(MapsHdr(start));
        Assert.DoesNotContain("av1", start.GetProperty("client_capabilities").GetProperty("codecs_video").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task AFileTheServerDoesNotStart_LeavesTheClaimOfTheOneThatPlays()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));
        _fixture.Silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Problem(404, "not_found"));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), ScriptedSilo.Problem(422, "validation_failed"),
            Created(StartedAgain));
        var playback = await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedHevc));
        _fixture.Engine.RaiseState(Frame(600));

        playback.SelectVersion("43");
        await playback.Recovery;
        Assert.False(StartMapsHdr(1));

        // The session is lost: the new attempt asks for the file that played, with its claim.
        await _fixture.AdvanceAsync(TimeSpan.FromSeconds(10));
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Post, StartPath).Count == 3, "a new attempt starts");
        Assert.Equal("42", _fixture.Body(HttpMethod.Post, StartPath, 2).GetProperty("file_id").GetString());
        Assert.True(StartMapsHdr(2));
    }

    [Fact]
    public async Task AStartAfterTheServerWasReinstalled_KeepsTheClaimForThePickedFile()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));
        _fixture.Silo.On(HttpMethod.Post, StartPath, ScriptedSilo.Problem(409, "installation_changed"), Created(PlaybackPayloads.StartOpaqueIds));

        await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedHevc));

        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, "api/v2/playback/capabilities").Count);
        Assert.True(StartMapsHdr(1));
    }

    [Fact]
    public async Task AStartAfterTheServerWasReinstalled_KeepsTheTitlesSoftwareDecodingChoice()
    {
        var reinstalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("vp9")));
        _fixture.Silo.On(HttpMethod.Post, StartPath, After(reinstalled.Task, ScriptedSilo.Problem(409, "installation_changed")),
            Created(PlaybackPayloads.StartOpaqueIds));
        var playback = _fixture.Create();

        var starting = playback.StartAsync(PlayRequest.Resume(MovieId), TestContext.Current.CancellationToken);
        await Until(() => _fixture.Silo.SentTo(HttpMethod.Post, StartPath).Count == 1, "the first start is sent");
        _fixture.DevicePreferences.DecodesInSoftware = true;
        reinstalled.SetResult();
        await starting;

        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Post, StartPath).Count);
        Assert.False(StartMapsHdr(1));
    }

    [Fact]
    public async Task AFileStartedAfterTheServerWasReinstalled_CarriesTheClaimForThatFile()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("vp9", "hevc")));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), ScriptedSilo.Problem(409, "installation_changed"),
            Created(StartedAgain));
        // A picked VP9 file, then the HEVC one.
        var playback = await _fixture.StartAsync(PlayRequest.Resume(MovieId, new PrePlayChoice("42", IsVersionPicked: true)));
        _fixture.Engine.RaiseState(Frame(600));
        Assert.False(StartMapsHdr());

        playback.SelectVersion("43");
        await playback.Recovery;

        Assert.Equal("43", _fixture.Body(HttpMethod.Post, StartPath, 2).GetProperty("file_id").GetString());
        Assert.True(StartMapsHdr(2));
    }
}
