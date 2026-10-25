namespace Granary.Core.Api.Playback;

// The string vocabularies of watch detail and playback protocol v3 (silo-server internal/playback/protocol_v3.go).
// Response values are open registries: compare against these, and treat a value not listed as unknown.

/// <summary>Values of <see cref="WatchDetail.Type"/>.</summary>
public static class WatchItemTypes
{
    public const string Movie = "movie";
    public const string Episode = "episode";
    public const string Audiobook = "audiobook";
    public const string Ebook = "ebook";
}

/// <summary>Values of <see cref="WatchMarkerSegment.Kind"/>.</summary>
public static class WatchMarkerKinds
{
    public const string Intro = "intro";
    public const string Credits = "credits";
    public const string Recap = "recap";
    public const string Preview = "preview";
}

/// <summary>Values of <see cref="WatchDetail.SubtitleMode"/>, the profile's subtitle preference.</summary>
public static class SubtitlePreferenceModes
{
    /// <summary>Subtitles only where they help, for example forced tracks when the audio is in the preferred language.</summary>
    public const string Auto = "auto";

    public const string Always = "always";

    public const string Off = "off";
}

/// <summary>Feature tokens of capability documents, decisions and <c>client_features</c> (docs/api-contract.md, 8.3).</summary>
public static class PlaybackFeatures
{
    /// <summary>The v3 plan operations exist. Required of the server; the client also names it.</summary>
    public const string PlaybackPlanV3 = "playback_plan_v3";

    /// <summary>Opaque plan keys the client only echoes, and intent replans. Required of the server.</summary>
    public const string NeutralPlaybackV3ContractV1 = "neutral_playback_v3_contract_v1";

    /// <summary>Progress and stop are sequenced (docs/api-contract.md, 10.1). Required of the capabilities document only.</summary>
    public const string SequencedProgressV1 = "sequenced_progress_v1";

    /// <summary>Media URLs carry no credential; every media request sends the bearer instead (docs/api-contract.md, 9.1).</summary>
    public const string HeaderAuthenticatedMediaV1 = "header_authenticated_media_v1";

    /// <summary>Native selection of embedded subtitles on direct play (docs/api-contract.md, 8.8).</summary>
    public const string EmbeddedSubtitlesV1 = "embedded_subtitles_v1";

    public const string SeekReanchorV1 = "seek_reanchor_v1";
    public const string OutputChangeV1 = "output_change_v1";

    /// <summary>An absent <c>source.duration_seconds</c> means unknown, not unsupported.</summary>
    public const string PlanSourceDurationV1 = "plan_source_duration_v1";

    /// <summary>An absent <c>active_quality</c> means the plan delivers no menu entry.</summary>
    public const string PlanActiveQualityV1 = "plan_active_quality_v1";

    // Features with client-side duties the client does not take on; named so code can say what it leaves out.
    public const string SoftwareVideoDecodeV1 = "software_video_decode_v1";
    public const string PlanInvalidatedV1 = "plan_invalidated_v1";
    public const string BufferingQualityV1 = "buffering_quality_v1";
    public const string SubripSidecarV1 = "subrip_sidecar_v1";
    public const string HlsSubtitleRenditionsV1 = "hls_subtitle_renditions_v1";
    public const string ClientVideoTransformationsV1 = "client_video_transformations_v1";
    public const string DeviceQuirksV1 = "device_quirks_v1";
    public const string AuthorizedMediaOriginsV1 = "authorized_media_origins_v1";
}

/// <summary>Claims a delivery class makes in <see cref="DeliveryCapability.ValidatedClaims"/>.</summary>
public static class DeliveryClaims
{
    /// <summary>The player switches audio tracks itself; without it a non-default audio track forces a remux.</summary>
    public const string ClientSelectedAudioTrackV1 = "client_selected_audio_track_v1";

    /// <summary>The player tone-maps HDR originals to the output itself (not claimed while output is SDR-only).</summary>
    public const string ClientManagedDynamicRangeV1 = "client_managed_dynamic_range_v1";
}

/// <summary>How a capability fact was obtained (docs/api-contract.md, 8.4, evidence tiers).</summary>
public static class CapabilityEvidence
{
    public const string Exact = "exact";
    public const string PlatformAttested = "platform_attested";

    /// <summary>Flat codec membership; the server checks no bounds.</summary>
    public const string Declared = "declared";
}

/// <summary>The keys of <see cref="ClientPlaybackContext.Deliveries"/>: the delivery classes a client can execute.</summary>
public static class DeliveryClasses
{
    /// <summary>Direct play of the original file.</summary>
    public const string OriginalHttp = "original_http";

    /// <summary>Forward-only fMP4 remux; not declared until the spike shows mpv copes.</summary>
    public const string Progressive = "progressive";

    /// <summary>HLS remux and transcode.</summary>
    public const string Hls = "hls";
}

/// <summary>Values of <see cref="PlaybackPlan.Delivery"/> and of the capability document's <c>deliveries</c>.</summary>
public static class PlaybackDeliveries
{
    public const string OriginalHttp = "original_http";
    public const string ServerRemuxProgressive = "server_remux_progressive";
    public const string ServerRemuxHls = "server_remux_hls";
    public const string ServerTranscodeHls = "server_transcode_hls";
}

/// <summary>Values of <see cref="PlaybackDecision.Outcome"/>.</summary>
public static class PlaybackOutcomes
{
    public const string Playable = "playable";

    /// <summary>A terminal decision: show its message, offer retry only when retryable, report a <c>terminal</c> route event.</summary>
    public const string AdaptationUnavailable = "adaptation_unavailable";
}

/// <summary>Values of <see cref="PlaybackStream.Protocol"/>.</summary>
public static class StreamProtocols
{
    public const string HttpProgressive = "http_progressive";
    public const string Hls = "hls";
}

/// <summary>Values of <see cref="PlaybackStream.HeaderRefresh"/>; servers send only <c>none</c> today.</summary>
public static class HeaderRefreshModes
{
    public const string None = "none";
    public const string Session = "session";
}

/// <summary>Values of <see cref="PlaybackTimeline.SeekRestoration"/>.</summary>
public static class SeekRestorations
{
    public const string PlayerPosition = "player_position";
    public const string SourcePosition = "source_position";
}

/// <summary>Values of <see cref="SubtitleDecision.Mode"/> (docs/api-contract.md, 8.8).</summary>
public static class SubtitleModes
{
    public const string Off = "off";

    /// <summary>The client renders the track: a sidecar <c>artifact</c>, an <c>embedded</c> stream or an HLS rendition.</summary>
    public const string Render = "render";

    /// <summary>A server WebVTT conversion as <c>artifact</c>.</summary>
    public const string Convert = "convert";

    /// <summary>The subtitles are in the video.</summary>
    public const string BurnIn = "burn_in";
}

/// <summary>Values of <see cref="SubtitleInventoryItem.Delivery"/>.</summary>
public static class SubtitleDeliveries
{
    /// <summary>Fetchable at the entry's <c>url</c> in every mode, so the player can switch it locally.</summary>
    public const string Sidecar = "sidecar";

    /// <summary>Only a <c>track_change</c> replan can show it.</summary>
    public const string BurnInOnly = "burn_in_only";
}

/// <summary>Values of <see cref="SubtitleInventoryItem.Source"/>.</summary>
public static class SubtitleSources
{
    public const string External = "external";
    public const string Embedded = "embedded";
    public const string Downloaded = "downloaded";
}

/// <summary>Values of <see cref="NativeEmbeddedSubtitleCapability.TrackIdentity"/>.</summary>
public static class SubtitleTrackIdentities
{
    /// <summary>The FFmpeg stream index; mpv's <c>track-list/N/ff-index</c>.</summary>
    public const string FfmpegStreamIndex = "ffmpeg_stream_index";

    public const string ContainerTrackId = "container_track_id";
}

/// <summary>Values of <c>quality_preference</c>; a rung is selected by its <see cref="AvailableQuality.Label"/>.</summary>
public static class QualityPreferences
{
    /// <summary>The source unless the device, a bandwidth limit or the server rules it out.</summary>
    public const string Auto = "auto";
}

/// <summary>Values of <c>subtitle_fidelity_preference</c>.</summary>
public static class SubtitleFidelity
{
    public const string Preserve = "preserve";
    public const string Compatible = "compatible";
}

/// <summary>Values of <see cref="PlaybackReplanRequest.Operation"/> (docs/api-contract.md, 8.7).</summary>
public static class ReplanOperations
{
    /// <summary>A route or decoder failure; needs a <see cref="PlaybackReplanRequest.Failure"/>. Never for a lost connection.</summary>
    public const string FailureRecovery = "failure_recovery";

    /// <summary>A seek failed; needs a failure, and quality, output and tracks must equal the current plan.</summary>
    public const string SeekFailureRecovery = "seek_failure_recovery";

    /// <summary>A seek outside the produced window; quality, output and tracks must equal the current plan.</summary>
    public const string SeekReanchor = "seek_reanchor";

    /// <summary>An audio or subtitle change the player cannot make locally; an omitted subtitle means off.</summary>
    public const string TrackChange = "track_change";

    /// <summary>A quality menu pick or a buffering step-down; needs a non-blank quality preference.</summary>
    public const string QualityChange = "quality_change";

    /// <summary>The display or audio output changed.</summary>
    public const string OutputChange = "output_change";

    /// <summary>The operations that carry a <see cref="PlaybackReplanRequest.Failure"/>; every other one must not.</summary>
    internal static bool NeedsFailure(string operation) => operation is FailureRecovery or SeekFailureRecovery;

    internal static bool ForbidsFailure(string operation) => operation is TrackChange or QualityChange or OutputChange;
}

/// <summary>
/// Failure classifications with server meaning (docs/api-contract.md, 8.7). Never use these as free text;
/// anything else is free-form diagnostics.
/// </summary>
public static class FailureClassifications
{
    /// <summary>A native embedded subtitle could not be mounted; the server stops offering native selection for the attempt.</summary>
    public const string SubtitleEmbeddedFailed = "subtitle_embedded_failed";

    /// <summary>An HLS subtitle rendition never became selectable.</summary>
    public const string SubtitleHlsRenditionFailed = "subtitle_hls_rendition_failed";

    /// <summary>An absolute proxy stream URL refused the client; the server retries the plan through the API origin.</summary>
    public const string SourceRefused = "sourceRefused";
}

/// <summary>Values of <see cref="PlaybackRouteEvent.Event"/> (docs/api-contract.md, 8.10).</summary>
public static class RouteEvents
{
    public const string PlanSelected = "plan_selected";
    public const string PlanInvalidated = "plan_invalidated";
    public const string PlanFailed = "plan_failed";
    public const string FirstFrame = "first_frame";

    /// <summary>A terminal decision; at start it carries no session or plan ids.</summary>
    public const string Terminal = "terminal";

    public const string Stopped = "stopped";
    public const string RuntimeCorrectionApplied = "runtime_correction_applied";
    public const string RuntimeCorrectionSucceeded = "runtime_correction_succeeded";
    public const string RuntimeCorrectionFailed = "runtime_correction_failed";
    public const string SeekReanchorRequested = "seek_reanchor_requested";
    public const string SeekReanchored = "seek_reanchored";
    public const string PlanSummary = "plan_summary";
}

/// <summary>
/// Some of the diagnostics keys the server keeps (silo-server <c>diagnosticKeysV3</c>); it drops any other key.
/// Values are strings of at most 256 characters.
/// </summary>
public static class RouteDiagnostics
{
    public const string DecoderName = "decoder_name";
    public const string FirstFrameMs = "first_frame_ms";
    public const string VideoCodecs = "video_codecs";
    public const string VideoWidth = "video_width";
    public const string VideoHeight = "video_height";
    public const string ErrorCode = "error_code";
    public const string AudioOutputMode = "audio_output_mode";
    public const string NetworkTransport = "network_transport";
}

/// <summary>Values of <see cref="PlaybackMutation.Outcome"/>.</summary>
public static class PlaybackMutationOutcomes
{
    /// <summary>Progress: the sample is the newest; it counts as a heartbeat.</summary>
    public const string Applied = "applied";

    /// <summary>Progress: the same sequence and sample again (it counts as a heartbeat). Stop: an earlier stop's receipt.</summary>
    public const string Replayed = "replayed";

    /// <summary>Progress: a newer sample is already committed (see <see cref="PlaybackMutation.Accepted"/>); not a heartbeat.</summary>
    public const string StaleSample = "stale_sample";

    /// <summary>Stop: this request ended the session.</summary>
    public const string Stopped = "stopped";
}
