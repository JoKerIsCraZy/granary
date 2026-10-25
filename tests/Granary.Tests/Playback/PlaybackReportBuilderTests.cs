using System.Text.Json.Nodes;
using Granary.Core.Api.Playback;

namespace Granary.Tests.Playback;

/// <summary>The capability report the mpv engine sends at start and on every replan (docs/api-contract.md, 13.2).</summary>
public sealed class PlaybackReportBuilderTests
{
    private static readonly PlaybackClientReport Report = PlaybackRequests.Report;

    [Fact]
    public void ClientFeatures_AreOnlyTheOnesTheClientHonours()
    {
        Assert.Equal(
            [PlaybackFeatures.PlaybackPlanV3, PlaybackFeatures.HeaderAuthenticatedMediaV1, PlaybackFeatures.EmbeddedSubtitlesV1],
            Report.ClientFeatures);
        Assert.DoesNotContain(PlaybackFeatures.SoftwareVideoDecodeV1, Report.ClientFeatures);
        Assert.DoesNotContain(PlaybackFeatures.BufferingQualityV1, Report.ClientFeatures);
        Assert.DoesNotContain(PlaybackFeatures.PlanInvalidatedV1, Report.ClientFeatures);
    }

    [Fact]
    public void Capabilities_DeclareWhatTheDeviceAndFfmpegDecode()
    {
        var capabilities = Report.ClientCapabilities;

        Assert.Equal((CapabilityEvidence.Declared, CapabilityEvidence.Declared), (capabilities.VideoEvidence, capabilities.AudioEvidence));
        Assert.Equal(["h264", "hevc", "av1", "vp9"], capabilities.CodecsVideo);
        Assert.Equal(["h264", "hevc"], capabilities.CodecsVideoHardware);
        Assert.Equal(PlaybackReportBuilder.AudioCodecs, capabilities.CodecsAudio);
        Assert.Contains("truehd", capabilities.CodecsAudio);
        Assert.Contains("dts", capabilities.CodecsAudio);
        Assert.Equal(["mkv", "mp4", "avi", "ts", "m2ts", "ogg", "flv", "wmv", "mpeg"], capabilities.Containers);
        Assert.False(capabilities.Hdr);
        Assert.Null(capabilities.MaxResolution);
    }

    [Fact]
    public void Capabilities_CodecListsAreLowerCaseAndDistinct()
    {
        var facts = PlaybackRequests.Facts with { HardwareVideoCodecs = ["H264", " HEVC ", "h264", ""], SoftwareVideoCodecs = ["VP9", "hevc"] };

        var capabilities = PlaybackReportBuilder.Build(facts, "1.0.0").ClientCapabilities;

        Assert.Equal(["vp9", "hevc", "h264"], capabilities.CodecsVideo);
        Assert.Equal(["h264", "hevc"], capabilities.CodecsVideoHardware);
    }

    [Fact]
    public void Capabilities_SoftwareOnlyMachineStillDeclaresItsCodecs()
    {
        var facts = PlaybackRequests.Facts with { HardwareVideoCodecs = [] };

        var capabilities = PlaybackReportBuilder.Build(facts, "1.0.0").ClientCapabilities;

        Assert.Equal(["h264", "hevc", "av1", "vp9"], capabilities.CodecsVideo);
        Assert.Empty(capabilities.CodecsVideoHardware);
    }

    [Fact]
    public void Capabilities_TooManyCodecsIsACallerBug()
    {
        var facts = PlaybackRequests.Facts with { SoftwareVideoCodecs = [.. Enumerable.Range(0, 65).Select(i => $"codec{i}")] };

        Assert.Throws<ArgumentException>(() => PlaybackReportBuilder.Build(facts, "1.0.0"));
    }

    [Fact]
    public void Context_DescribesADesktopWithEmptyOutput()
    {
        var context = Report.ClientPlaybackContext;

        Assert.Equal((3, "desktop", "0.1.0", "dev"), (context.ProtocolVersion, context.FormFactor, context.AppVersion, context.AppChannel));
        Assert.Null(context.AppBuild);
        Assert.Equal(new DeviceContext { Platform = "windows", OsVersion = "10.0.26100" }, context.Device);
        Assert.Equal(new OutputContext(), context.Output);
    }

    [Fact]
    public void Context_DeviceStringsAreTrimmedToTheServersBounds()
    {
        var facts = PlaybackRequests.Facts with { OsVersion = "  ", Manufacturer = new string('m', 200), Model = " Surface ", Platform = new string('p', 40) };

        var device = PlaybackReportBuilder.Build(facts, "1.0.0", " ").ClientPlaybackContext;

        Assert.Null(device.Device.OsVersion);
        Assert.Equal(128, device.Device.Manufacturer!.Length);
        Assert.Equal("Surface", device.Device.Model);
        Assert.Equal(32, device.Device.Platform!.Length);
        Assert.Null(device.AppChannel);
    }

    [Fact]
    public void Context_RequiresAnAppVersion() =>
        Assert.Throws<ArgumentException>(() => PlaybackReportBuilder.Build(PlaybackRequests.Facts, " "));

    [Fact]
    public void Deliveries_AreOriginalHttpAndHlsWithoutProgressive()
    {
        var deliveries = Report.ClientPlaybackContext.Deliveries;

        Assert.Equal([DeliveryClasses.Hls, DeliveryClasses.OriginalHttp], deliveries.Keys.Order(StringComparer.Ordinal));
        Assert.All(deliveries.Values, d =>
        {
            Assert.True(d.Enabled);
            Assert.True(d.SupportedOnDevice);
            Assert.Empty(d.AudioPassthroughCodecs);
            Assert.Empty(d.Features);
            Assert.Empty(d.Transformations);
            Assert.False(d.AuthHeaderRefresh);
            Assert.Null(d.MaxChannels);
            Assert.Equal(Report.ClientCapabilities.CodecsVideo, d.VideoCodecs);
            Assert.Equal(Report.ClientCapabilities.CodecsAudio, d.AudioDecodeCodecs);
        });
    }

    [Fact]
    public void OriginalHttp_ClaimsAudioSelectionAndNativeEmbeddedSubtitles()
    {
        var original = Report.ClientPlaybackContext.Deliveries[DeliveryClasses.OriginalHttp];

        Assert.Equal([DeliveryClaims.ClientSelectedAudioTrackV1], original.ValidatedClaims);
        Assert.Equal(Report.ClientCapabilities.Containers, original.Containers);
        var subtitles = original.Subtitles;
        Assert.Equal((true, true, true, true, false, true),
            (subtitles.EmbeddedText, subtitles.SidecarText, subtitles.AssStyling, subtitles.EmbeddedBitmap, subtitles.SidecarBitmap, subtitles.FontAttachments));
        var native = subtitles.NativeEmbedded!;
        Assert.Equal(["mkv", "mp4"], native.Select(n => n.Container));
        Assert.All(native, n => Assert.Equal(SubtitleTrackIdentities.FfmpegStreamIndex, n.TrackIdentity));
        Assert.All(native, n => Assert.True(n.AssStyling && n.FontAttachments));
        Assert.Equal(["subrip", "ass", "ssa", "webvtt", "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle"], native[0].Codecs);
        Assert.Equal(["mov_text"], native[1].Codecs);
    }

    [Fact]
    public void Hls_IsUnconstrainedByContainerWithoutNativeOrClaims()
    {
        var hls = Report.ClientPlaybackContext.Deliveries[DeliveryClasses.Hls];

        Assert.Empty(hls.Containers);
        Assert.Empty(hls.ValidatedClaims);
        Assert.Null(hls.Subtitles.NativeEmbedded);
        Assert.Equal((true, true, true, true, false, true),
            (hls.Subtitles.EmbeddedText, hls.Subtitles.SidecarText, hls.Subtitles.AssStyling, hls.Subtitles.EmbeddedBitmap, hls.Subtitles.SidecarBitmap,
                hls.Subtitles.FontAttachments));
    }

    /// <summary>
    /// The members each object of a start body may carry, from the v2 OpenAPI (<c>additionalProperties: false</c>
    /// everywhere): <c>*</c> marks a required member.
    /// </summary>
    private static readonly Dictionary<string, string[]> Schema = new()
    {
        ["start"] = ["*installation_id", "*protocol_version", "*client_features", "*file_id", "*profile_id", "*playback_attempt_id", "*quality_preference",
            "*subtitle_fidelity_preference", "*client_capabilities", "*client_playback_context", "allow_alternate_versions", "audio_track_id",
            "audio_track_index", "bandwidth_cap_kbps", "bandwidth_estimate_kbps", "bandwidth_estimate_source", "learned_limit", "metered",
            "observed_throughput_kbps", "progress_persistence", "start_position", "subtitle_track_id", "subtitle_track_index"],
        ["client_capabilities"] = ["*audio_evidence", "*codecs_audio", "*codecs_video", "*codecs_video_hardware", "*containers", "*hdr", "*video_evidence",
            "audio_passthrough", "hdr_details", "max_resolution", "video_decode"],
        ["client_playback_context"] = ["*app_version", "*deliveries", "*device", "*form_factor", "*output", "*protocol_version", "app_build", "app_channel"],
        ["device"] = ["manufacturer", "model", "os_version", "platform", "platform_details"],
        ["output"] = ["audio_passthrough", "current_sink", "display", "hdr_details", "output_context_id", "sink_type"],
        ["delivery"] = ["*audio_decode_codecs", "*audio_passthrough_codecs", "*auth_header_refresh", "*containers", "*enabled", "*features", "*subtitles",
            "*supported_on_device", "*transformations", "*validated_claims", "*video_codecs", "failure_reason", "hdr_details", "max_channels"],
        ["subtitles"] = ["*ass_styling", "*embedded_bitmap", "*embedded_text", "*font_attachments", "*sidecar_bitmap", "*sidecar_text", "native_embedded"],
        ["native_embedded"] = ["*ass_styling", "*codecs", "*container", "*font_attachments", "*track_identity"],
    };

    [Fact]
    public void StartBody_BuiltFromTheReportCarriesOnlySchemaMembers()
    {
        var body = JsonNode.Parse(PlaybackRequests.Json(PlaybackRequests.Start()))!.AsObject();

        AssertShape("start", body);
        AssertShape("client_capabilities", body["client_capabilities"]!.AsObject());
        var context = body["client_playback_context"]!.AsObject();
        AssertShape("client_playback_context", context);
        AssertShape("device", context["device"]!.AsObject());
        AssertShape("output", context["output"]!.AsObject());
        var deliveries = context["deliveries"]!.AsObject();
        Assert.Equal(["original_http", "hls"], deliveries.Select(d => d.Key));
        foreach (var (_, delivery) in deliveries)
        {
            AssertShape("delivery", delivery!.AsObject());
            var subtitles = delivery["subtitles"]!.AsObject();
            AssertShape("subtitles", subtitles);
            foreach (var native in subtitles["native_embedded"]?.AsArray() ?? [])
            {
                AssertShape("native_embedded", native!.AsObject());
            }
        }
        AssertNoNulls(body);
        AssertListsLowerCase(body["client_capabilities"]!.AsObject());
    }

    [Fact]
    public void ReplanBody_CarriesTheSameReport()
    {
        var start = JsonNode.Parse(PlaybackRequests.Json(PlaybackRequests.Start()))!.AsObject();
        var replan = JsonNode.Parse(PlaybackRequests.Json(PlaybackRequests.Replan()))!.AsObject();

        Assert.True(JsonNode.DeepEquals(start["client_features"], replan["client_features"]));
        Assert.True(JsonNode.DeepEquals(start["client_capabilities"], replan["client_capabilities"]));
        Assert.True(JsonNode.DeepEquals(start["client_playback_context"], replan["client_playback_context"]));
    }

    private static void AssertShape(string schema, JsonObject value)
    {
        var allowed = Schema[schema].Select(m => m.TrimStart('*')).ToHashSet(StringComparer.Ordinal);
        var required = Schema[schema].Where(m => m[0] == '*').Select(m => m[1..]);
        Assert.All(value.Select(p => p.Key), key => Assert.True(allowed.Contains(key), $"{schema} must not carry '{key}'"));
        Assert.All(required, key => Assert.True(value.ContainsKey(key), $"{schema} must carry '{key}'"));
    }

    private static void AssertNoNulls(JsonNode? node)
    {
        switch (node)
        {
            case null:
                Assert.Fail("The body carries a null.");
                break;
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    AssertNoNulls(child);
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    AssertNoNulls(child);
                }
                break;
        }
    }

    private static void AssertListsLowerCase(JsonObject capabilities)
    {
        foreach (string list in new[] { "codecs_video", "codecs_video_hardware", "codecs_audio", "containers" })
        {
            Assert.All(capabilities[list]!.AsArray(), v => Assert.Equal(((string)v!).ToLowerInvariant(), (string)v!));
        }
    }
}
