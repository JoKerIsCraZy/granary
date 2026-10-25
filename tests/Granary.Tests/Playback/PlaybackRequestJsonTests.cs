using System.Text.Json.Nodes;
using Granary.Core.Api.Playback;

namespace Granary.Tests.Playback;

/// <summary>
/// The v2 request bodies are strict (unknown members are 422): required members always present, empty
/// objects as <c>{}</c>, optional members omitted rather than null, ids as strings (docs/api-contract.md 8.4, 8.7, 8.10, 10.1, 10.3).
/// </summary>
public sealed class PlaybackRequestJsonTests
{
    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static void AssertJson(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), $"Expected {expected}\nActual   {actual}");

    [Fact]
    public void Start_ContractMinimalShapeSerializesAsDocumented()
    {
        var request = new PlaybackStartRequest
        {
            InstallationId = PlaybackPayloads.InstallationId,
            ClientFeatures = [PlaybackFeatures.PlaybackPlanV3, PlaybackFeatures.HeaderAuthenticatedMediaV1],
            FileId = "42",
            ProfileId = "p1",
            PlaybackAttemptId = PlaybackRequests.AttemptId,
            ClientCapabilities = new ClientCapabilities
            {
                VideoEvidence = CapabilityEvidence.Declared,
                AudioEvidence = CapabilityEvidence.Declared,
                CodecsVideo = ["h264", "hevc"],
                CodecsVideoHardware = ["h264", "hevc"],
                CodecsAudio = ["aac", "ac3", "eac3"],
                Containers = ["mkv", "mp4"],
            },
            ClientPlaybackContext = new ClientPlaybackContext
            {
                FormFactor = "desktop",
                AppVersion = "0.1.0",
                Device = new DeviceContext { Platform = "windows" },
                Deliveries = new Dictionary<string, DeliveryCapability>
                {
                    [DeliveryClasses.OriginalHttp] = new()
                    {
                        Enabled = true,
                        SupportedOnDevice = true,
                        Subtitles = new DeliverySubtitleCapabilities
                        {
                            EmbeddedText = true, SidecarText = true, AssStyling = true, EmbeddedBitmap = true, FontAttachments = true,
                        },
                    },
                },
            },
        };

        AssertJson($$"""
            {
              "installation_id": "{{PlaybackPayloads.InstallationId}}", "protocol_version": 3,
              "client_features": ["playback_plan_v3", "header_authenticated_media_v1"],
              "file_id": "42", "profile_id": "p1", "playback_attempt_id": "{{PlaybackRequests.AttemptId}}",
              "quality_preference": "auto", "subtitle_fidelity_preference": "preserve",
              "client_capabilities": {
                "video_evidence": "declared", "audio_evidence": "declared",
                "codecs_video": ["h264", "hevc"], "codecs_video_hardware": ["h264", "hevc"],
                "codecs_audio": ["aac", "ac3", "eac3"], "containers": ["mkv", "mp4"], "hdr": false
              },
              "client_playback_context": {
                "protocol_version": 3, "form_factor": "desktop", "app_version": "0.1.0",
                "device": { "platform": "windows" }, "output": {},
                "deliveries": {
                  "original_http": {
                    "enabled": true, "supported_on_device": true, "containers": [], "video_codecs": [],
                    "audio_decode_codecs": [], "audio_passthrough_codecs": [],
                    "subtitles": { "embedded_text": true, "sidecar_text": true, "ass_styling": true,
                                   "embedded_bitmap": true, "sidecar_bitmap": false, "font_attachments": true },
                    "features": [], "auth_header_refresh": false, "validated_claims": [], "transformations": []
                  }
                }
              }
            }
            """, PlaybackRequests.Json(request));
    }

    [Fact]
    public void Start_UnsetOptionalMembersAreLeftOut()
    {
        var request = PlaybackRequests.Start() with { StartPosition = null, AudioTrackIndex = null };

        var body = Parse(PlaybackRequests.Json(request));

        Assert.Equal(
            ["installation_id", "protocol_version", "client_features", "file_id", "profile_id", "playback_attempt_id",
                "quality_preference", "subtitle_fidelity_preference", "client_capabilities", "client_playback_context"],
            body.Select(p => p.Key));
        Assert.Equal("42", (string?)body["file_id"]);
        Assert.Equal(3, (int?)body["protocol_version"]);
        Assert.Equal(3, (int?)body["client_playback_context"]!["protocol_version"]);
    }

    [Fact]
    public void Start_SetOptionalMembersAreSent()
    {
        var request = PlaybackRequests.Start() with
        {
            QualityPreference = "1080p",
            SubtitleFidelityPreference = SubtitleFidelity.Compatible,
            AllowAlternateVersions = false,
            StartPosition = 0,
            AudioTrackId = "file:42:audio:1",
            AudioTrackIndex = 1,
            SubtitleTrackId = "file:42:subtitle:3",
            SubtitleTrackIndex = 3,
            BandwidthEstimateKbps = 25000,
            BandwidthCapKbps = 20000,
        };

        var body = Parse(PlaybackRequests.Json(request));

        Assert.Equal("1080p", (string?)body["quality_preference"]);
        Assert.Equal("compatible", (string?)body["subtitle_fidelity_preference"]);
        Assert.False((bool)body["allow_alternate_versions"]!);
        // 0 is "start over", not "resume": it must reach the server (docs/api-contract.md, 10.5).
        Assert.Equal(0d, (double)body["start_position"]!);
        Assert.Equal(("file:42:audio:1", 1), ((string?)body["audio_track_id"], (int?)body["audio_track_index"]));
        Assert.Equal(("file:42:subtitle:3", 3), ((string?)body["subtitle_track_id"], (int?)body["subtitle_track_index"]));
        Assert.Equal((25000, 20000), ((int?)body["bandwidth_estimate_kbps"], (int?)body["bandwidth_cap_kbps"]));
    }

    [Fact]
    public void Start_NullListsGoOutEmpty()
    {
        var request = PlaybackRequests.Start() with { ClientFeatures = null! };

        Assert.Equal("[]", Parse(PlaybackRequests.Json(request))["client_features"]!.ToJsonString());
    }

    [Fact]
    public void Replan_RequiredMembersAreAlwaysPresent()
    {
        var request = PlaybackRequests.Replan() with { AttemptedPlanKeys = [] };

        var body = Parse(PlaybackRequests.Json(request));

        Assert.Equal(
            ["installation_id", "protocol_version", "client_features", "operation", "playback_attempt_id", "replan_request_id",
                "failed_plan_id", "plan_attempt_id", "plan_attempt_key", "attempted_plan_keys", "attempt_count",
                "quality_preference", "position_seconds", "selected_tracks", "failure", "client_capabilities", "client_playback_context"],
            body.Select(p => p.Key));
        Assert.Equal("[]", body["attempted_plan_keys"]!.ToJsonString());
        Assert.Equal("{}", body["selected_tracks"]!.ToJsonString());
        Assert.Equal("""{"classification":"decoder_init_failed","decoder_name":"hevc_d3d11va"}""", body["failure"]!.ToJsonString());
        Assert.Equal("auto", (string?)body["quality_preference"]);
        Assert.Equal(300d, (double)body["position_seconds"]!);
    }

    [Fact]
    public void Replan_EchoesPlanKeysAndSendsTheTracks()
    {
        var request = PlaybackRequests.Replan() with
        {
            Operation = ReplanOperations.TrackChange,
            Failure = null,
            AttemptedPlanKeys = ["v3:f0144c47fa349e3e", "v3:AbCdEf0123456789"],
            LocalMutations = ["transport_reopen"],
            SelectedTracks = new SelectedTracks { Audio = new TrackIdentity("file:42:audio:1", 1), Subtitle = new TrackIdentity("file:42:subtitle:3") },
        };

        var body = Parse(PlaybackRequests.Json(request));

        Assert.False(body.ContainsKey("failure"));
        Assert.Equal("""["v3:f0144c47fa349e3e","v3:AbCdEf0123456789"]""", body["attempted_plan_keys"]!.ToJsonString());
        Assert.Equal("""["transport_reopen"]""", body["local_mutations"]!.ToJsonString());
        Assert.Equal("""{"audio":{"id":"file:42:audio:1","index":1},"subtitle":{"id":"file:42:subtitle:3"}}""", body["selected_tracks"]!.ToJsonString());
    }

    [Fact]
    public void Progress_SendsTheSampleFlat()
    {
        var request = PlaybackProgressRequest.For(PlaybackPayloads.InstallationId, new PlaybackSample(7, 61.25, true));

        Assert.Equal($$"""{"installation_id":"{{PlaybackPayloads.InstallationId}}","sequence":7,"position":61.25,"is_paused":true}""",
            PlaybackRequests.Json(request));
    }

    [Fact]
    public void Stop_FinalSampleMembersTravelTogether()
    {
        var withSample = new PlaybackStopRequest { InstallationId = "i", StopId = "s", FinalSample = new PlaybackSample(3, 0, false) };
        var without = withSample with { FinalSample = null };

        Assert.Equal("""{"installation_id":"i","stop_id":"s","sequence":3,"position":0,"is_paused":false}""", PlaybackRequests.Json(withSample));
        Assert.Equal("""{"installation_id":"i","stop_id":"s"}""", PlaybackRequests.Json(without));
    }

    [Fact]
    public void RouteEvent_StartTerminalCarriesNoSessionOrPlanAndEmptyDiagnostics()
    {
        var routeEvent = new PlaybackRouteEvent
        {
            InstallationId = PlaybackPayloads.InstallationId,
            EventId = "55555555-5555-4555-8555-555555555555",
            PlaybackAttemptId = PlaybackRequests.AttemptId,
            Event = RouteEvents.Terminal,
            FallbackReason = "route_preparation_failed",
        };

        Assert.Equal(
            $$$"""{"installation_id":"{{{PlaybackPayloads.InstallationId}}}","event_id":"55555555-5555-4555-8555-555555555555","protocol_version":3,"playback_attempt_id":"{{{PlaybackRequests.AttemptId}}}","event":"terminal","fallback_reason":"route_preparation_failed","diagnostics":{}}""",
            PlaybackRequests.Json(routeEvent));
    }

    [Fact]
    public void RouteEvent_CarriesThePlanIdsAndDiagnostics()
    {
        var body = Parse(PlaybackRequests.Json(PlaybackRequests.FirstFrame()));

        Assert.Equal(
            ["installation_id", "event_id", "protocol_version", "playback_attempt_id", "session_id", "plan_id", "plan_attempt_id", "plan_attempt_key",
                "event", "diagnostics"],
            body.Select(p => p.Key));
        Assert.Equal("""{"first_frame_ms":"840"}""", body["diagnostics"]!.ToJsonString());
    }

    [Fact]
    public void Context_EmptyDeviceAndOutputAreObjects()
    {
        var context = new ClientPlaybackContext { FormFactor = "desktop", AppVersion = "1", Device = null!, Output = null!, Deliveries = null! };
        var request = PlaybackRequests.Start() with { ClientPlaybackContext = context };

        var body = Parse(PlaybackRequests.Json(request))["client_playback_context"]!;

        Assert.Equal("""{"protocol_version":3,"form_factor":"desktop","app_version":"1","device":{},"output":{},"deliveries":{}}""", body.ToJsonString());
    }
}
