using System.Text.Json.Nodes;
using Granary.Core.Api.Playback;

namespace Granary.Tests.Playback;

/// <summary>
/// The report's video evidence (docs/api-contract.md, 13.2): where the server takes software decoders, Granary attests its
/// pinned libmpv and FFmpeg build at <c>platform_attested</c> with one <c>video_decode</c> entry per decoder, so a server that
/// trusts only probed decoders with Matroska still plays an MKV directly.
/// </summary>
public sealed class AttestedDecodersTests
{
    // The light codecs MpvDeviceProbe gives the CPU.
    private static readonly string[] CpuCodecs = ["h264", "mpeg2video", "mpeg4", "msmpeg4v3", "vc1", "wmv3", "vp8", "mjpeg"];

    // A current GPU: every HDR codec at 10 bits.
    private static readonly PlaybackDeviceFacts Modern = PlaybackRequests.Facts with
    {
        HardwareVideoCodecs = ["h264", "hevc", "vp9", "av1", "mpeg2video", "vc1"],
        HardwareTenBitVideoCodecs = ["hevc", "vp9", "av1"],
        SoftwareVideoCodecs = CpuCodecs,
    };

    // No AV1 decoder in the GPU.
    private static readonly PlaybackDeviceFacts NoAv1 = Modern with
    {
        HardwareVideoCodecs = ["h264", "hevc", "vp9"],
        HardwareTenBitVideoCodecs = ["hevc", "vp9"],
    };

    // A Skylake laptop: HEVC and VP9 at 8 bits only.
    private static readonly PlaybackDeviceFacts EightBitOnly = Modern with
    {
        HardwareVideoCodecs = ["h264", "hevc", "vp9"],
        HardwareTenBitVideoCodecs = [],
    };

    // No GPU decoder at all (a remote session, a basic display driver).
    private static readonly PlaybackDeviceFacts CpuOnly = Modern with { HardwareVideoCodecs = [], HardwareTenBitVideoCodecs = [] };

    private static PlaybackClientReport Attested(PlaybackDeviceFacts facts, bool softwareDecoding = false) =>
        PlaybackReportBuilder.Build(facts, "1.0.0", softwareDecoding: softwareDecoding, attestsDecoders: true);

    private static PlaybackClientReport Declared(PlaybackDeviceFacts facts, bool softwareDecoding = false) =>
        PlaybackReportBuilder.Build(facts, "1.0.0", softwareDecoding: softwareDecoding);

    [Fact]
    public void WithoutTheServersSoftwareDecoders_TheReportStaysDeclared()
    {
        var report = Declared(Modern);

        Assert.Equal((CapabilityEvidence.Declared, CapabilityEvidence.Declared),
            (report.ClientCapabilities.VideoEvidence, report.ClientCapabilities.AudioEvidence));
        Assert.Null(report.ClientCapabilities.VideoDecode);
        Assert.DoesNotContain(PlaybackFeatures.SoftwareVideoDecodeV1, report.ClientFeatures);
    }

    [Fact]
    public void Attested_VideoIsPlatformAttested_AudioStaysDeclared_AndSoftwareDecodersAreOptedIn()
    {
        var report = Attested(Modern);

        Assert.Equal((CapabilityEvidence.PlatformAttested, CapabilityEvidence.Declared),
            (report.ClientCapabilities.VideoEvidence, report.ClientCapabilities.AudioEvidence));
        Assert.Equal([.. PlaybackReportBuilder.ClientFeatures, PlaybackFeatures.SoftwareVideoDecodeV1], report.ClientFeatures);
    }

    [Fact]
    public void Attested_ListsAndDeliveriesStayAsDeclared()
    {
        var attested = Attested(Modern);
        var declared = Declared(Modern);

        var (a, d) = (attested.ClientCapabilities, declared.ClientCapabilities);
        Assert.Equal(d.CodecsVideo, a.CodecsVideo);
        Assert.Equal(d.CodecsVideoHardware, a.CodecsVideoHardware);
        Assert.Equal(d.CodecsAudio, a.CodecsAudio);
        Assert.Equal(d.Containers, a.Containers);
        Assert.Equal((d.Hdr, d.MaxResolution), (a.Hdr, a.MaxResolution));
        Assert.Equal(declared.ClientPlaybackContext.Deliveries[DeliveryClasses.OriginalHttp].VideoCodecs,
            attested.ClientPlaybackContext.Deliveries[DeliveryClasses.OriginalHttp].VideoCodecs);
        Assert.Equal(declared.ClientPlaybackContext.Deliveries[DeliveryClasses.OriginalHttp].ValidatedClaims,
            attested.ClientPlaybackContext.Deliveries[DeliveryClasses.OriginalHttp].ValidatedClaims);
        Assert.Equal(declared.ClientPlaybackContext.Deliveries[DeliveryClasses.Hls].VideoCodecs,
            attested.ClientPlaybackContext.Deliveries[DeliveryClasses.Hls].VideoCodecs);
    }

    [Fact]
    public void Attested_GpuDecodersCarryTheProbedBitDepths()
    {
        var decoders = Attested(NoAv1).ClientCapabilities.VideoDecode!.Where(d => d.Hardware);

        Assert.Equal(
            [("h264", "8"), ("hevc", "8,10"), ("vp9", "8,10")],
            decoders.Select(d => (d.Codec, string.Join(',', d.BitDepths!))));
    }

    [Fact]
    public void Attested_CpuDecodersAreFfmpegsWithoutABitDepthBound()
    {
        var decoders = Attested(NoAv1).ClientCapabilities.VideoDecode!.Where(d => !d.Hardware).ToList();

        Assert.Equal(CpuCodecs, decoders.Select(d => d.Codec));
        Assert.All(decoders, d => Assert.Null(d.BitDepths));
    }

    [Fact]
    public void Attested_Av1WithoutAGpuDecoder_IsACpuDecoder_WhereTheCpuListHasIt()
    {
        var decoders = Attested(NoAv1 with { SoftwareVideoCodecs = [.. CpuCodecs, "av1"] }).ClientCapabilities.VideoDecode!;

        var av1 = Assert.Single(decoders, d => d.Codec == "av1");
        Assert.False(av1.Hardware);
        Assert.Null(av1.BitDepths);
    }

    [Fact]
    public void Attested_SoftwareDecodingAddsCpuDecodersForHevcVp9AndAv1()
    {
        var decoders = Attested(EightBitOnly, softwareDecoding: true).ClientCapabilities.VideoDecode!;

        Assert.Equal(["hevc", "vp9", "av1"], decoders.Where(d => !d.Hardware && d.Codec is "hevc" or "vp9" or "av1").Select(d => d.Codec));
        Assert.Contains(decoders, d => d.Codec == "hevc" && d.Hardware);
    }

    public static TheoryData<string, bool> EveryDevice() => new()
    {
        { nameof(Modern), false }, { nameof(Modern), true },
        { nameof(NoAv1), false }, { nameof(NoAv1), true },
        { nameof(EightBitOnly), false }, { nameof(EightBitOnly), true },
        { nameof(CpuOnly), false }, { nameof(CpuOnly), true },
        { "Fixture", false }, { "Fixture", true },
    };

    private static PlaybackDeviceFacts Device(string name) => name switch
    {
        nameof(Modern) => Modern,
        nameof(NoAv1) => NoAv1,
        nameof(EightBitOnly) => EightBitOnly,
        nameof(CpuOnly) => CpuOnly,
        _ => PlaybackRequests.Facts,
    };

    [Theory]
    [MemberData(nameof(EveryDevice))]
    public void Attested_EveryListedCodecHasADecoderTheServerCounts(string device, bool softwareDecoding)
    {
        // A strict tier refuses a listed codec without an entry (playback-protocol-v3.md, "A flat claim without backing
        // detail is a refusal"); a CPU entry counts only with software_video_decode_v1.
        var report = Attested(Device(device), softwareDecoding);
        var capabilities = report.ClientCapabilities;

        Assert.Contains(PlaybackFeatures.SoftwareVideoDecodeV1, report.ClientFeatures);
        Assert.All(capabilities.CodecsVideo.Concat(capabilities.CodecsVideoHardware),
            codec => Assert.Contains(capabilities.VideoDecode!, d => d.Codec == codec));
        Assert.All(capabilities.VideoDecode!, d => Assert.Contains(d.Codec, capabilities.CodecsVideo));
        Assert.All(capabilities.CodecsVideoHardware, codec => Assert.Contains(capabilities.VideoDecode!, d => d.Codec == codec && d.Hardware));
    }

    [Fact]
    public void Attested_TooManyDecodersIsACallerBug()
    {
        var many = Enumerable.Range(0, 40).Select(i => $"codec{i}").ToArray();

        Assert.Throws<ArgumentException>(() => Attested(Modern with { HardwareVideoCodecs = many, SoftwareVideoCodecs = many }));
    }

    // A source as Silo Server's scanner records it: the planner's routing facts (codec, bit depth) that the entries bound.
    public sealed record Source(string Name, string Codec, int BitDepth);

    private static readonly Source H264 = new("1080p H.264 8-bit", "h264", 8);
    private static readonly Source H264High10 = new("1080p H.264 High 10", "h264", 10);
    private static readonly Source Hevc8 = new("1080p HEVC Main", "hevc", 8);
    private static readonly Source Hdr10 = new("4K HEVC Main 10 HDR10", "hevc", 10);
    private static readonly Source DolbyVision7 = new("4K Dolby Vision profile 7", "hevc", 10);
    private static readonly Source DolbyVision8 = new("4K Dolby Vision profile 8.1", "hevc", 10);
    private static readonly Source DolbyVision5 = new("4K Dolby Vision profile 5", "hevc", 10);
    private static readonly Source Av110 = new("4K AV1 10-bit", "av1", 10);
    private static readonly Source Vp9 = new("VP9 Profile 0", "vp9", 8);
    private static readonly Source Mpeg2 = new("DVD MPEG-2", "mpeg2video", 8);
    private static readonly Source Vc1 = new("Blu-ray VC-1", "vc1", 8);

    private static readonly Source[] Sources = [H264, H264High10, Hevc8, Hdr10, DolbyVision7, DolbyVision8, DolbyVision5, Av110, Vp9, Mpeg2, Vc1];

    /// <summary>
    /// Silo Server's video check for a direct or copy route (<c>videoEligibleV3</c> with <c>videoDecodeTakesSourceV3</c>,
    /// internal/playback/capabilities_v3.go), for the members this report sends. <c>declared</c>: the flat lists decide.
    /// <c>exact</c> and <c>platform_attested</c>: an entry for the codec decides, a CPU entry only with
    /// <c>software_video_decode_v1</c>; a hardware entry's profiles and levels are skipped at <c>platform_attested</c>, and an
    /// empty <c>bit_depths</c> or a missing <c>max_*</c> bound is unconstrained.
    /// </summary>
    private static bool SiloPlaysVideo(PlaybackClientReport report, Source source)
    {
        var capabilities = report.ClientCapabilities;
        if (capabilities.VideoEvidence == CapabilityEvidence.Declared)
        {
            return capabilities.CodecsVideo.Contains(source.Codec) || capabilities.CodecsVideoHardware.Contains(source.Codec);
        }
        bool softwareOptIn = report.ClientFeatures.Contains(PlaybackFeatures.SoftwareVideoDecodeV1);
        return (capabilities.VideoDecode ?? []).Any(d =>
            d.Codec == source.Codec && (d.Hardware || softwareOptIn) && (d.BitDepths is not { Count: > 0 } depths || depths.Contains(source.BitDepth)));
    }

    [Fact]
    public void Attested_TypicalSourcesStayDirectlyPlayable_OnACurrentGpu()
    {
        var report = Attested(Modern);

        Assert.All(Sources, source => Assert.True(SiloPlaysVideo(report, source), source.Name));
    }

    [Fact]
    public void Attested_HdrAndDolbyVisionKeepTheirToneMappingClaim_WhereTheGpuDecodesHevcAtTenBits()
    {
        // The claim lets the server send HDR originals on original_http; the entry lets it take the 10-bit HEVC video.
        var report = PlaybackReportBuilder.Build(Modern, "1.0.0", hdrFileCodecs: ["hevc"], attestsDecoders: true);

        var original = report.ClientPlaybackContext.Deliveries[DeliveryClasses.OriginalHttp];
        Assert.Equal([DeliveryClaims.ClientSelectedAudioTrackV1, DeliveryClaims.ClientManagedDynamicRangeV1, .. DeliveryClaims.ClientManagedDolbyVisionBaseLayersV1],
            original.ValidatedClaims);
        Assert.All([Hdr10, DolbyVision7, DolbyVision8, DolbyVision5], source => Assert.True(SiloPlaysVideo(report, source), source.Name));
    }

    [Theory]
    [MemberData(nameof(EveryDevice))]
    public void Attested_PlaysTheSameVideoDirectlyAsDeclared_ApartFromTenBitHdrCodecsTheGpuDecodesAtEightBitsOnly(string device, bool softwareDecoding)
    {
        var facts = Device(device);
        var attested = Attested(facts, softwareDecoding);
        var declared = Declared(facts, softwareDecoding);
        var tenBitGpu = facts.HardwareTenBitVideoCodecs;
        var cpu = facts.SoftwareVideoCodecs;

        foreach (var source in Sources)
        {
            // An 8-bit GPU decoder the CPU list does not back: the CPU would decode a 10-bit original, which at declared
            // evidence the server could not tell apart. HDR in it is the server's already (DecodesHdr).
            bool leftToTheServer = !softwareDecoding && source.BitDepth == 10 && facts.HardwareVideoCodecs.Contains(source.Codec)
                && !tenBitGpu.Contains(source.Codec) && !cpu.Contains(source.Codec);
            Assert.Equal(SiloPlaysVideo(declared, source) && !leftToTheServer, SiloPlaysVideo(attested, source));
        }
    }

    [Fact]
    public void Attested_TenBitHevcOnAnEightBitGpu_IsTheServers_UnlessTheViewerChoseSoftwareDecoding()
    {
        Assert.True(SiloPlaysVideo(Attested(EightBitOnly), Hevc8));
        Assert.False(SiloPlaysVideo(Attested(EightBitOnly), Hdr10));
        Assert.True(SiloPlaysVideo(Attested(EightBitOnly, softwareDecoding: true), Hdr10));
        Assert.False(MapsHdr(Attested(EightBitOnly)));
    }

    [Fact]
    public void Attested_H264High10_PlaysThroughFfmpeg_ThoughTheGpuDecodesEightBitH264Only()
    {
        var report = Attested(Modern);

        Assert.DoesNotContain(report.ClientCapabilities.VideoDecode!, d => d.Codec == "h264" && d.Hardware && d.BitDepths!.Contains(10));
        Assert.True(SiloPlaysVideo(report, H264High10));
    }

    [Fact]
    public void Attested_Av1_PlaysThroughFfmpeg_OnlyWhereTheCpuListOrTheViewerHasIt()
    {
        Assert.False(SiloPlaysVideo(Attested(NoAv1), Av110));
        Assert.True(SiloPlaysVideo(Attested(NoAv1 with { SoftwareVideoCodecs = [.. CpuCodecs, "av1"] }), Av110));
        Assert.True(SiloPlaysVideo(Attested(NoAv1, softwareDecoding: true), Av110));
        Assert.True(SiloPlaysVideo(Attested(CpuOnly), H264));
    }

    private static bool MapsHdr(PlaybackClientReport report) =>
        report.ClientPlaybackContext.Deliveries[DeliveryClasses.OriginalHttp].ValidatedClaims.Contains(DeliveryClaims.ClientManagedDynamicRangeV1);

    [Fact]
    public void StartBody_WritesEachDecoderAsTheServerSpellsIt()
    {
        var start = PlaybackRequests.Start() with
        {
            ClientFeatures = Attested(NoAv1).ClientFeatures,
            ClientCapabilities = Attested(NoAv1).ClientCapabilities,
        };

        var body = JsonNode.Parse(PlaybackRequests.Json(start))!.AsObject();

        var capabilities = body["client_capabilities"]!.AsObject();
        Assert.Equal("platform_attested", (string)capabilities["video_evidence"]!);
        Assert.Equal("declared", (string)capabilities["audio_evidence"]!);
        var decoders = capabilities["video_decode"]!.AsArray().Select(d => d!.AsObject()).ToList();
        Assert.All(decoders, d => Assert.Subset(new HashSet<string>(["codec", "hardware", "bit_depths"]), d.Select(m => m.Key).ToHashSet()));
        Assert.Equal("""{"codec":"hevc","hardware":true,"bit_depths":[8,10]}""", decoders.Single(d => (string)d["codec"]! == "hevc").ToJsonString());
        Assert.Equal("""{"codec":"h264","hardware":false}""", decoders.Last(d => (string)d["codec"]! == "h264").ToJsonString());
        Assert.Contains("software_video_decode_v1", body["client_features"]!.AsArray().Select(f => (string)f!));
    }

    [Fact]
    public void StartBody_Declared_CarriesNoDecoders()
    {
        var body = JsonNode.Parse(PlaybackRequests.Json(PlaybackRequests.Start()))!.AsObject();

        Assert.False(body["client_capabilities"]!.AsObject().ContainsKey("video_decode"));
    }
}
