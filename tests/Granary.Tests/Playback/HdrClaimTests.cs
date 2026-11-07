using System.Text.Json;
using System.Text.RegularExpressions;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Tests.Profiles;
using static Granary.Tests.Playback.ServerPlaybackFixture;

namespace Granary.Tests.Playback;

/// <summary>
/// Whether a start claims to tone-map HDR itself follows the codecs of the HDR files the server may send for it: the file
/// started, and the title's other files unless the start keeps to it (docs/api-contract.md, 13.2).
/// </summary>
public sealed class HdrClaimTests
{
    private const string WatchPath = "api/v2/watch/" + MovieId;

    // The end of the watch fixture's versions, where a second file goes.
    private const string EndOfVersions = @"\}(\s*\],\s*""playback_variants"")";

    // A GPU that decodes HEVC at 10 bits and VP9 only at 8, as several common cards report.
    private static readonly PlaybackDeviceFacts EightBitVp9 = new FakeDeviceProbe().Facts with
    {
        HardwareVideoCodecs = ["h264", "hevc", "vp9"],
        HardwareTenBitVideoCodecs = ["hevc"],
        SoftwareVideoCodecs = ["h264", "mpeg2video"],
    };

    // The other way round: VP9 at 10 bits, HEVC only at 8.
    private static readonly PlaybackDeviceFacts EightBitHevc = EightBitVp9 with { HardwareTenBitVideoCodecs = ["vp9"] };

    // File 42, picked on the detail page: the server may send no other.
    private static readonly PrePlayChoice PickedFirst = new("42", IsVersionPicked: true);

    private readonly ServerPlaybackFixture _fixture = new();

    public HdrClaimTests()
    {
        _fixture.Device.Facts = EightBitVp9;
    }

    // The movie as file 42 and, with a second codec, a 4K file 43. Each is HDR unless said otherwise; null leaves the
    // server's hdr flag out.
    private static string Title(string codec, string? otherCodec = null, bool? hdr = true, bool? otherHdr = true)
    {
        string watch = Replaced(PlaybackPayloads.WatchStateOk, "\"codec_video\": \"h264\"", $"\"codec_video\": \"{codec}\"");
        watch = Replaced(watch, "\"hdr\": false,", HdrMember(hdr));
        if (otherCodec is null)
        {
            return watch;
        }
        Assert.Single(Regex.Matches(watch, EndOfVersions));
        return Regex.Replace(watch, EndOfVersions,
            "}, { \"file_id\": \"43\", \"resolution\": \"2160p\", \"codec_video\": \"" + otherCodec + "\", \"codec_audio\": \"eac3\", "
            + HdrMember(otherHdr) + " \"container\": \"mkv\", \"file_size\": 2048, \"duration_seconds\": 10200, \"bitrate\": 16000000 }$1",
            RegexOptions.None);
    }

    private static string HdrMember(bool? hdr) => hdr is { } value ? $"\"hdr\": {(value ? "true" : "false")}," : "";

    // The fixture's one occurrence of text replaced: a fixture that changed fails here, not as a claim that is wrong for
    // another reason.
    private static string Replaced(string json, string text, string with)
    {
        int at = json.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0 && at == json.LastIndexOf(text, StringComparison.Ordinal), $"The watch fixture must hold {text} once.");
        return json.Replace(text, with, StringComparison.Ordinal);
    }

    private bool StartMapsHdr(int index = 0) => MapsHdr(_fixture.Body(HttpMethod.Post, StartPath, index));

    private static bool MapsHdr(JsonElement body) =>
        body.GetProperty("client_playback_context").GetProperty("deliveries").GetProperty("original_http").GetProperty("validated_claims")
            .EnumerateArray().Any(c => c.GetString() == DeliveryClaims.ClientManagedDynamicRangeV1);

    // Whether watch's start claims tone mapping on another PC.
    private static async Task<bool> StartMapsHdrOn(PlaybackDeviceFacts device, string watch)
    {
        var fixture = new ServerPlaybackFixture();
        fixture.Device.Facts = device;
        fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(watch));
        await fixture.StartAsync();
        return MapsHdr(fixture.Body(HttpMethod.Post, StartPath));
    }

    // What the latest report in the log says about the title's HDR.
    private string LoggedHdr()
    {
        const string Hdr = "HDR originals of this title are ";
        string line = _fixture.Logs.Lines.Last(l => l.Contains("This PC plays", StringComparison.Ordinal)).TrimEnd();
        int at = line.IndexOf(Hdr, StringComparison.Ordinal);
        return at < 0 ? line : line[(at + Hdr.Length)..];
    }

    [Fact]
    public async Task AnHevcTitle_ClaimsToneMapping_ThoughTheVp9DecoderIsEightBitOnly()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc")));

        await _fixture.StartAsync();

        Assert.True(StartMapsHdr());
        Assert.Equal("played directly", LoggedHdr());
        // The claim is HEVC's: on a card that decodes HEVC only at 8 bits, the same title gets none (an H.264 one would).
        Assert.False(await StartMapsHdrOn(EightBitHevc, Title("hevc")));
        Assert.True(await StartMapsHdrOn(EightBitHevc, Title("h264")));
    }

    [Fact]
    public async Task AVp9Title_LeavesHdrToTheServer_WhenTheVp9DecoderIsEightBitOnly()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("vp9")));

        await _fixture.StartAsync();

        Assert.False(StartMapsHdr());
        Assert.Equal("tone-mapped by the server", LoggedHdr());
    }

    [Fact]
    public async Task ATitleWithAVp9HdrVersionTheServerMayPick_LeavesHdrToTheServer()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));

        await _fixture.StartAsync();

        var start = _fixture.Body(HttpMethod.Post, StartPath);
        Assert.Equal("42", start.GetProperty("file_id").GetString());
        Assert.False(start.TryGetProperty("allow_alternate_versions", out _));
        Assert.False(MapsHdr(start));
    }

    [Fact]
    public async Task AnSdrVp9VersionTheServerMayPick_KeepsTheClaimForTheHevcHdrFile()
    {
        // An SDR file needs no 10-bit decoder, so the 8-bit VP9 one plays it.
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9", otherHdr: false)));

        await _fixture.StartAsync();

        Assert.True(StartMapsHdr());
    }

    [Fact]
    public async Task AVersionThatDoesNotSayWhetherItIsHdr_CountsAsHdr()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9", otherHdr: null)));

        await _fixture.StartAsync();

        Assert.False(StartMapsHdr());
    }

    [Fact]
    public async Task AnHdrFileWithoutAKnownCodec_ChecksEveryHdrCodecTheReportLists()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("")));

        await _fixture.StartAsync();

        Assert.False(StartMapsHdr());
    }

    [Fact]
    public async Task AnSdrFileWithoutAKnownCodec_DoesNotCount()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "", otherHdr: false)));

        await _fixture.StartAsync();

        Assert.True(StartMapsHdr());
    }

    [Fact]
    public async Task APickedHevcVersion_ClaimsToneMapping_ThoughTheTitleHasAVp9Version()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));

        await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedFirst));

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
        var playback = await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedFirst));
        _fixture.Engine.RaiseState(Frame(600));
        Assert.True(StartMapsHdr());

        playback.SelectVersion("43");
        await playback.Recovery;

        var start = _fixture.Body(HttpMethod.Post, StartPath, 1);
        Assert.Equal("43", start.GetProperty("file_id").GetString());
        Assert.False(MapsHdr(start));
        Assert.Equal("tone-mapped by the server", LoggedHdr());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFileSwitchedToFromAKeptFile_IsKeptToo_WithTheClaimForItsOwnCodec(bool watchParty)
    {
        // The VP9 file first, then the HEVC one: a report that still counted the VP9 file would claim nothing.
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("vp9", "hevc")));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await _fixture.StartAsync(watchParty ? PlayRequest.WatchParty(MovieId, "42", selectionRevision: 1) : PlayRequest.Resume(MovieId, PickedFirst));
        _fixture.Engine.RaiseState(Frame(600));
        Assert.False(StartMapsHdr());

        playback.SelectVersion("43");
        await playback.Recovery;

        var start = _fixture.Body(HttpMethod.Post, StartPath, 1);
        Assert.Equal("43", start.GetProperty("file_id").GetString());
        Assert.False(start.GetProperty("allow_alternate_versions").GetBoolean());
        Assert.True(MapsHdr(start));
    }

    [Fact]
    public async Task AFileSwitchedTo_KeepsTheTitlesSoftwareDecodingChoice()
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedFirst));
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
    public async Task AFileSwitchedTo_KeepsAskingForMediaFromTheServersNodes()
    {
        _fixture.Silo.On(HttpMethod.Get, "api/v2/playback/capabilities", Ok(PlaybackPayloads.CapabilityAvailable.Replace(
            "\"connection_speed_v1\"", "\"connection_speed_v1\", \"authorized_media_origins_v1\"", StringComparison.Ordinal)));
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), Created(StartedAgain));
        var playback = await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedFirst));
        _fixture.Engine.RaiseState(Frame(600));
        Assert.Contains(PlaybackFeatures.AuthorizedMediaOriginsV1, ClientFeatures(0));

        playback.SelectVersion("43");
        await playback.Recovery;

        Assert.Contains(PlaybackFeatures.AuthorizedMediaOriginsV1, ClientFeatures(1));
    }

    private IEnumerable<string?> ClientFeatures(int start) =>
        _fixture.Body(HttpMethod.Post, StartPath, start).GetProperty("client_features").EnumerateArray().Select(f => f.GetString());

    [Fact]
    public Task AFileTheServerRefusesToStart_LeavesTheClaimOfTheOneThatPlays() =>
        SwitchIsTurnedDownAsync(ScriptedSilo.Problem(422, "validation_failed"));

    [Fact]
    public Task AFileTheServerHasNoStreamOf_LeavesTheClaimOfTheOneThatPlays() =>
        SwitchIsTurnedDownAsync(Created(PlaybackPayloads.TerminalDecision));

    // The picked HEVC file plays; the server turns down the switch to the VP9 one with answer.
    private async Task SwitchIsTurnedDownAsync(Func<Task<HttpResponseMessage>> answer)
    {
        _fixture.Silo.On(HttpMethod.Get, WatchPath, Ok(Title("hevc", "vp9")));
        _fixture.Silo.On(HttpMethod.Post, ProgressPath, ScriptedSilo.Problem(404, "not_found"));
        _fixture.Silo.On(HttpMethod.Post, StartPath, Created(PlaybackPayloads.StartOpaqueIds), answer, Created(StartedAgain));
        var playback = await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedFirst));
        _fixture.Engine.RaiseState(Frame(600));

        playback.SelectVersion("43");
        await playback.Recovery;

        Assert.Equal(ServerPlaybackPhase.Playing, playback.Status.Phase);
        Assert.False(StartMapsHdr(1));
        // The log names the report of the file that plays on, not the one turned down.
        Assert.Equal("played directly", LoggedHdr());

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

        await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedFirst));

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
        var playback = await _fixture.StartAsync(PlayRequest.Resume(MovieId, PickedFirst));
        _fixture.Engine.RaiseState(Frame(600));
        Assert.False(StartMapsHdr());

        playback.SelectVersion("43");
        await playback.Recovery;

        Assert.Equal("43", _fixture.Body(HttpMethod.Post, StartPath, 2).GetProperty("file_id").GetString());
        Assert.True(StartMapsHdr(2));
    }
}
