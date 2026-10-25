using System.Text.Json;
using Granary.Core.Api.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>The capability document's gate (docs/api-contract.md 8.3) and the decisions of start and replan (8.5, 8.6, 8.8, 9.2, 9.6).</summary>
public sealed class PlaybackDecisionTests
{
    private static PlaybackDecision Decision(string json) => JsonSerializer.Deserialize(json, PlaybackJsonContext.Default.PlaybackDecision)!;

    private static PlaybackCapabilities Capabilities(string json) => JsonSerializer.Deserialize(json, PlaybackJsonContext.Default.PlaybackCapabilities)!;

    [Fact]
    public void Capabilities_AvailableFixturePassesTheGate()
    {
        var capabilities = Capabilities(PlaybackPayloads.CapabilityAvailable);

        Assert.True(capabilities.IsUsable);
        Assert.Equal(("available", true, PlaybackPayloads.InstallationId), (capabilities.State, capabilities.Allowed, capabilities.InstallationId));
        Assert.Equal("60ba3ba311d3a4e17db759b1a599a21bd1cfd308dafeffabc1da489d1b895a32", capabilities.Revision);
        Assert.Equal([3], capabilities.ProtocolVersions);
        Assert.Equal(["original_http", "server_transcode_hls"], capabilities.Deliveries);
        Assert.True(capabilities.HasFeature(PlaybackFeatures.SequencedProgressV1));
        Assert.False(capabilities.HasFeature(PlaybackFeatures.HeaderAuthenticatedMediaV1));
        Assert.Equal(113750, capabilities.BandwidthProbeTargetKbps);
        Assert.Equal(604800, capabilities.BandwidthProbeMaxAgeSeconds);
        Assert.True(capabilities.BandwidthProbeAutomatic);
        Assert.Equal("http://api.example.test/api/v2/playback/bandwidth-probe?media_path=true", capabilities.BandwidthProbeUrl);
    }

    [Fact]
    public void Capabilities_UnconfiguredFixtureFailsTheGate()
    {
        var capabilities = Capabilities(PlaybackPayloads.CapabilityUnconfigured);

        Assert.False(capabilities.IsUsable);
        Assert.Null(capabilities.InstallationId);
        Assert.Empty(capabilities.Features);
    }

    public static TheoryData<string> GateBreakers => new()
    {
        """{ "state": "disabled" }""",
        """{ "state": "unsupported" }""",
        """{ "state": "something_new" }""",
        """{ "allowed": false }""",
        """{ "protocol_versions": [2, 4] }""",
        """{ "installation_id": "" }""",
        """{ "features": ["playback_plan_v3", "neutral_playback_v3_contract_v1"] }""",
        """{ "features": ["playback_plan_v3", "sequenced_progress_v1"] }""",
        """{ "features": ["neutral_playback_v3_contract_v1", "sequenced_progress_v1"] }""",
    };

    [Theory]
    [MemberData(nameof(GateBreakers))]
    public void Capabilities_GateNeedsEveryCondition(string change)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(PlaybackPayloads.CapabilityAvailable)!.AsObject();
        foreach (var (key, value) in System.Text.Json.Nodes.JsonNode.Parse(change)!.AsObject())
        {
            document[key] = value?.DeepClone();
        }

        Assert.False(Capabilities(document.ToJsonString()).IsUsable);
    }

    [Fact]
    public void Capabilities_FeatureNamesMatchLikeTheServers()
    {
        var capabilities = Capabilities("""{ "features": [" Playback_Plan_V3 "] }""");

        Assert.True(capabilities.HasFeature(PlaybackFeatures.PlaybackPlanV3));
    }

    [Fact]
    public void Start_DirectPlayFixtureKeepsEveryOpaqueIdVerbatim()
    {
        var decision = Decision(PlaybackPayloads.StartOpaqueIds);

        Assert.True(decision.IsCompatible);
        Assert.True(decision.IsPlayable);
        Assert.Equal(PlaybackPayloads.SessionId, decision.SessionId);
        Assert.Null(decision.Terminal);
        var plan = decision.PlaybackPlan!;
        Assert.Equal(3, plan.ProtocolVersion);
        Assert.Equal("plan:478677870860e5e5108c18bff749b34b", plan.PlanId);
        Assert.Equal("v3:f0144c47fa349e3e", plan.PlanAttemptKey);
        Assert.Equal(PlaybackPayloads.SessionId, plan.SessionId);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), plan.ExpiresAt);
        Assert.Equal(PlaybackDeliveries.OriginalHttp, plan.Delivery);
        Assert.Equal(("42", "42", "42"), (plan.RequestedMediaFileId, plan.EffectiveMediaFileId, plan.Source.MediaFileId));
        Assert.Equal("validated_original_playback", plan.DecisionReason);
        Assert.Equal("allow_simplified_rendering", plan.SubtitleFidelityPolicy);
        Assert.Empty(plan.Transformations);
        Assert.Empty(plan.AppliedQuirks);
        Assert.Empty(plan.RuntimeCorrections);
        Assert.Empty(plan.AvailableQualities);
        Assert.Empty(plan.DegradationWarnings);
        Assert.Null(plan.ActiveQuality);
        Assert.Null(plan.BandwidthLimit);
        Assert.False(plan.RequiresClientExecution);
    }

    [Fact]
    public void Start_DirectPlayFixtureReadsStreamTimelineAndRecipe()
    {
        var plan = Decision(PlaybackPayloads.StartOpaqueIds).PlaybackPlan!;

        var stream = plan.Stream;
        Assert.Equal("/api/v2/stream/" + PlaybackPayloads.SessionId, stream.Url);
        Assert.Equal((StreamProtocols.HttpProgressive, "mp4", "video/mp4", HeaderRefreshModes.None), (stream.Protocol, stream.Container, stream.MimeType, stream.HeaderRefresh));
        Assert.Empty(stream.Headers);
        Assert.Null(stream.HeaderRefreshUrl);

        var timeline = plan.Timeline;
        Assert.Equal((12.5, 0d, 12.5, 0d), (timeline.SourceStartSeconds, timeline.StreamOriginSeconds, timeline.PlayerStartSeconds, timeline.TimelineOffsetSeconds));
        Assert.True(timeline.CanSeekAnywhere);
        Assert.Equal(SeekRestorations.PlayerPosition, timeline.SeekRestoration);
        Assert.Null(timeline.SeekWindowStartSeconds);
        Assert.Null(timeline.SeekWindowEndSeconds);

        Assert.Equal(new TrackIdentity("file:42:audio:0", 0), plan.SelectedTracks.Audio);
        Assert.Null(plan.SelectedTracks.Subtitle);

        var recipe = plan.EffectiveRecipe;
        Assert.Equal(("h264", "aac", 1920, 1080, 8000, "sdr", 2, "stereo"),
            (recipe.VideoCodec, recipe.AudioCodec, recipe.Width, recipe.Height, recipe.BitrateKbps, recipe.DynamicRange, recipe.AudioChannels, recipe.AudioLayout));
        Assert.Equal(23.976023976023978, recipe.FrameRate);

        Assert.Equal(("aac", false, "client_decode_supported"), (plan.Claims.Audio.Codec, plan.Claims.Audio.Passthrough, plan.Claims.Audio.Reason));
        Assert.False(plan.Claims.Video.Hdr10);
        Assert.False(plan.Claims.Subtitles.AssStylingPreserved);

        var source = plan.Source;
        Assert.Equal((7200d, "mp4", "h264", "high", 41, 8), (source.DurationSeconds, source.Container, source.VideoCodec, source.VideoProfile, source.VideoLevel, source.BitDepth));
        Assert.Equal(("sdr", false, "none", "aac", 2, "stereo"),
            (source.DynamicRange, source.Hdr10Plus, source.DvEnhancementLayer, source.AudioCodec, source.AudioChannels, source.AudioLayout));
    }

    [Fact]
    public void Start_DirectPlayFixtureReadsTheSubtitleInventory()
    {
        var subtitle = Decision(PlaybackPayloads.StartOpaqueIds).PlaybackPlan!.Subtitle;

        Assert.Equal(SubtitleModes.Off, subtitle.Mode);
        Assert.Null(subtitle.TrackId);
        Assert.Null(subtitle.SelectedTrack);
        Assert.Null(subtitle.Artifact);
        Assert.Null(subtitle.Embedded);
        Assert.Null(subtitle.HlsRendition);
        Assert.Equal([0, 1, 2, 3, 4], subtitle.Inventory.Select(i => i.CombinedIndex));

        var external = subtitle.Inventory[0];
        Assert.Equal(("file:42:subtitle:0", SubtitleSources.External, "srt", "eng", "English", SubtitleDeliveries.Sidecar),
            (external.TrackId, external.Source, external.Codec, external.Language, external.Label, external.Delivery));
        Assert.Equal("/api/v2/stream/" + PlaybackPayloads.SessionId + "/subtitles/0.vtt?file_id=42&external_subtitle_key=f903db1c5624c46c4a2c20f2fd8cd247fc0d46e6471c91d4ddb651ae4e039448",
            external.Url);
        Assert.Equal("external-f903db1c5624c46c4a2c20f2fd8cd247fc0d46e6471c91d4ddb651ae4e039448", external.SyncKey);

        var signs = subtitle.Inventory[1];
        Assert.True(signs.Forced);
        Assert.Equal("/api/v2/stream/" + PlaybackPayloads.SessionId + "/subtitles/1/fonts?file_id=42&embedded_stream_index=0", signs.FontBundleUrl);

        var burnIn = subtitle.Inventory[3];
        Assert.Equal((SubtitleDeliveries.BurnInOnly, (string?)null), (burnIn.Delivery, burnIn.Url));
        Assert.Equal(SubtitleSources.Downloaded, subtitle.Inventory[4].Source);
    }

    [Fact]
    public void Transcode_ReadsTheShiftedTimelineQualitiesAndConvertedSidecar()
    {
        var decision = Decision(PlaybackPayloads.TranscodeDecision);

        Assert.True(decision.IsCompatible);
        Assert.True(decision.IsPlayable);
        var plan = decision.PlaybackPlan!;
        Assert.Null(plan.SessionId);
        Assert.Null(plan.ExpiresAt);
        Assert.Equal(PlaybackDeliveries.ServerTranscodeHls, plan.Delivery);
        Assert.Equal((StreamProtocols.Hls, (string?)null), (plan.Stream.Protocol, plan.Stream.Container));
        Assert.Equal("1", plan.Stream.Headers["X-Test"]);

        var timeline = plan.Timeline;
        Assert.False(timeline.CanSeekAnywhere);
        Assert.Equal(SeekRestorations.SourcePosition, timeline.SeekRestoration);
        Assert.Equal((598d, (double?)null), (timeline.SeekWindowStartSeconds, timeline.SeekWindowEndSeconds));
        Assert.Equal(600, timeline.ToSource(2));
        Assert.Equal(2, timeline.ToPlayer(600));

        Assert.Equal(new TrackIdentity("file:9:audio:1"), plan.SelectedTracks.Audio);
        Assert.Equal(new TrackIdentity("file:9:subtitle:0", 0), plan.SelectedTracks.Subtitle);
        Assert.Equal(("9", "10"), (plan.RequestedMediaFileId, plan.EffectiveMediaFileId));
        Assert.Null(plan.Source.DurationSeconds);
        Assert.Equal("hdr10", plan.Source.DynamicRange);
        Assert.Equal("output_sdr", plan.Claims.Video.DolbyVisionReason);

        var subtitle = plan.Subtitle;
        Assert.Equal(SubtitleModes.Convert, subtitle.Mode);
        Assert.Same(subtitle.Inventory[0], subtitle.SelectedTrack);
        var artifact = subtitle.Artifact!;
        Assert.Equal(("text/vtt", "vtt", 0d), (artifact.MimeType, artifact.Format, artifact.TimingOriginSeconds));

        Assert.Equal(["1080p-high", "720p-medium"], plan.AvailableQualities.Select(q => q.Label));
        Assert.Equal(("720p Medium", 720, 4000, false),
            (plan.AvailableQualities[1].DisplayName, plan.AvailableQualities[1].Height, plan.AvailableQualities[1].BitrateKbps, plan.AvailableQualities[1].PreservesSource));
        Assert.Equal("720p-medium", plan.ActiveQuality);
        Assert.Equal(new DegradationWarning("hdr_tone_mapped", "HDR was tone mapped to SDR."), Assert.Single(plan.DegradationWarnings));
        Assert.Equal(["video_to_h264", "hdr_to_sdr_tonemap"], plan.Transformations.Select(t => t.Name));
        Assert.Equal(["h264_decode"], plan.Transformations[0].ValidatedClaims);
        Assert.False(plan.RequiresClientExecution);
        Assert.Equal((6000, "observed_rebuffer"), (plan.BandwidthLimit!.Kbps, plan.BandwidthLimit.Source));
    }

    [Fact]
    public void Claims_ReadEveryFlagByItsWireName()
    {
        var claims = Decision("""
            { "outcome": "playable", "playback_plan": { "claims": {
              "video": { "hdr10": true, "hdr10_plus": true, "hlg": true, "dolby_vision": true },
              "audio": { "passthrough": true, "atmos_preserved": true, "dts_variant": "dts_hd_ma" },
              "subtitles": { "ass_styling_preserved": true, "bitmap_overlay": true, "bitmap_sidecar": true } },
              "source": { "hdr10_plus": true, "dolby_vision_profile": 8, "dolby_vision_level": 6, "dv_bl_compat_id": 1, "dv_base_layer_proven": true, "video_copy_unsafe": true } } }
            """).PlaybackPlan!;

        Assert.Equal((true, true, true, true), (claims.Claims.Video.Hdr10, claims.Claims.Video.Hdr10Plus, claims.Claims.Video.Hlg, claims.Claims.Video.DolbyVision));
        Assert.Equal((true, true, "dts_hd_ma"), (claims.Claims.Audio.Passthrough, claims.Claims.Audio.AtmosPreserved, claims.Claims.Audio.DtsVariant));
        Assert.Equal((true, true, true), (claims.Claims.Subtitles.AssStylingPreserved, claims.Claims.Subtitles.BitmapOverlay, claims.Claims.Subtitles.BitmapSidecar));
        Assert.Equal((true, 8, 6, 1, true, true),
            (claims.Source.Hdr10Plus, claims.Source.DolbyVisionProfile, claims.Source.DolbyVisionLevel, claims.Source.DvBlCompatId, claims.Source.DvBaseLayerProven,
                claims.Source.VideoCopyUnsafe));
    }

    [Fact]
    public void Terminal_IsADecisionWithoutAPlan()
    {
        var decision = Decision(PlaybackPayloads.TerminalDecision);

        Assert.True(decision.IsCompatible);
        Assert.False(decision.IsPlayable);
        Assert.Equal(PlaybackOutcomes.AdaptationUnavailable, decision.Outcome);
        Assert.Null(decision.SessionId);
        Assert.Null(decision.PlaybackPlan);
        Assert.Equal(new PlaybackTerminal { Reason = "route_preparation_failed", Message = "The stream could not be prepared.", Retryable = true }, decision.Terminal);
    }

    [Theory]
    [InlineData("""{ "protocol_version": 2, "server_features": ["playback_plan_v3", "neutral_playback_v3_contract_v1"], "outcome": "playable" }""")]
    [InlineData("""{ "protocol_version": 3, "server_features": ["playback_plan_v3"], "outcome": "playable" }""")]
    [InlineData("""{ "protocol_version": 3, "server_features": ["neutral_playback_v3_contract_v1"], "outcome": "playable" }""")]
    [InlineData("""{ "protocol_version": 3, "outcome": "playable" }""")]
    public void Decision_WithoutProtocolThreeAndItsFeaturesIsIncompatible(string json) =>
        Assert.False(Decision(json).IsCompatible);

    [Fact]
    public void Decision_PlayableNeedsAPlanAndASession()
    {
        Assert.False(Decision("""{ "protocol_version": 3, "outcome": "playable", "session_id": "s" }""").IsPlayable);
        Assert.False(Decision("""{ "protocol_version": 3, "outcome": "playable", "playback_plan": { "plan_id": "p" } }""").IsPlayable);
    }

    [Fact]
    public void Plan_MissingMembersReadAsEmpty()
    {
        var plan = Decision("""{ "outcome": "playable", "playback_plan": {} }""").PlaybackPlan!;

        Assert.Equal(("", "", ""), (plan.PlanId, plan.PlanAttemptKey, plan.Delivery));
        Assert.Equal("", plan.Stream.Url);
        Assert.Empty(plan.Stream.Headers);
        Assert.Empty(plan.Subtitle.Inventory);
        Assert.Equal("", plan.Subtitle.Mode);
        Assert.Null(plan.SelectedTracks.Audio);
        Assert.Equal("", plan.Source.MediaFileId);
        Assert.Empty(plan.Transformations);
    }

    [Fact]
    public void Plan_PositionalMembersReadMissingStringsAsEmpty()
    {
        var plan = Decision("""
            { "outcome": "playable", "playback_plan": { "selected_tracks": { "audio": { "index": 2 }, "subtitle": { "id": null } }, "degradation_warnings": [ { "code": "x" } ] } }
            """).PlaybackPlan!;

        Assert.Equal(new TrackIdentity("", 2), plan.SelectedTracks.Audio);
        Assert.Equal("", plan.SelectedTracks.Subtitle!.Id);
        Assert.Equal(new DegradationWarning("x", ""), Assert.Single(plan.DegradationWarnings));
    }

    [Fact]
    public void Plan_ClientSideWorkMustBeRejected()
    {
        var clientTransform = Decision("""
            { "outcome": "playable", "playback_plan": { "transformations": [ { "name": "client_dv7_to_hdr10", "executor": "client", "recipe_version": "1", "validated_claims": [] } ] } }
            """).PlaybackPlan!;
        var correction = Decision("""{ "outcome": "playable", "playback_plan": { "runtime_corrections": ["client_surface_recovery_v1"] } }""").PlaybackPlan!;

        Assert.True(clientTransform.RequiresClientExecution);
        Assert.True(correction.RequiresClientExecution);
    }

    [Theory]
    [InlineData("/api/v2/stream/s1", "https://nas.example/silo/api/v2/stream/s1")]
    [InlineData("/api/v2/stream/s1/subtitles/0.vtt?file_id=42&external_subtitle_key=a%2Bb", "https://nas.example/silo/api/v2/stream/s1/subtitles/0.vtt?file_id=42&external_subtitle_key=a%2Bb")]
    [InlineData("https://nas.example/silo/api/v2/playback/transcode/s1/master.m3u8", "https://nas.example/silo/api/v2/playback/transcode/s1/master.m3u8")]
    public void MediaUrl_ResolvesOnTheServerAndKeepsTheQuery(string value, string expected) =>
        Assert.Equal(expected, PlaybackUrl.Resolve(ScriptedSilo.Base, value)!.OriginalString);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//evil.example/api/v2/stream/s1")]
    [InlineData("https://proxy.example/api/v2/stream/s1")]
    [InlineData("https://nas.example/other/api/v2/stream/s1")]
    [InlineData("file:///C:/media.mkv")]
    public void MediaUrl_OffTheServerIsRefused(string? value) =>
        Assert.Null(PlaybackUrl.Resolve(ScriptedSilo.Base, value));

    [Theory]
    [InlineData("https://node.example/stream/v3/s1")]
    [InlineData("https://node.example:8443/stream/v3/s1?seek=600.5")]
    [InlineData("https://node.example/stream/v3/s1/master.m3u8")]
    public void AProxyNodesStream_IsTakenOnceTheAttemptAgreedOnIt(string value)
    {
        Assert.Equal(value, PlaybackUrl.ResolveStream(ScriptedSilo.Base, value, "s1", mediaOrigins: true)!.OriginalString);
        Assert.Null(PlaybackUrl.ResolveStream(ScriptedSilo.Base, value, "s1", mediaOrigins: false));
    }

    [Theory]
    [InlineData("http://node.example/stream/v3/s1")]
    [InlineData("https://user:pw@node.example/stream/v3/s1")]
    [InlineData("https://node.example/stream/v3/s2")]
    [InlineData("https://node.example/prefix/stream/v3/s1")]
    [InlineData("https://node.example/stream/v3/s1/../../api/v2/users")]
    [InlineData("https://node.example/stream/v3/s1?seek=600&token=x")]
    [InlineData("https://node.example/stream/v3/s1#x")]
    [InlineData("https://node.example/api/v2/stream/s1")]
    public void AnythingElseOnAProxyNode_IsRefused(string value)
    {
        // Plain HTTP while the server is HTTPS, user info, another session, other paths or queries (Apple checks the same).
        Assert.Null(PlaybackUrl.ResolveStream(ScriptedSilo.Base, value, "s1", mediaOrigins: true));
        Assert.True(PlaybackUrl.IsElsewhere(ScriptedSilo.Base, value));
    }

    [Fact]
    public void TheServersOwnStream_StaysWhatItWas() =>
        Assert.Equal("https://nas.example/silo/api/v2/stream/s1",
            PlaybackUrl.ResolveStream(ScriptedSilo.Base, "/api/v2/stream/s1", "s1", mediaOrigins: true)!.OriginalString);
}
