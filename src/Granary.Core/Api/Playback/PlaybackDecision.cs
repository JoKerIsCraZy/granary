using System.Text.Json.Serialization;

namespace Granary.Core.Api.Playback;

/// <summary>
/// The answer of start (201) and replan (200), docs/api-contract.md 8.5: a <see cref="PlaybackPlan"/> to execute,
/// or a <see cref="Terminal"/> explanation. Check <see cref="IsCompatible"/> on every decision.
/// </summary>
public sealed record PlaybackDecision
{
    public int ProtocolVersion { get; init; }

    /// <summary>The features this decision was made under (<see cref="PlaybackFeatures"/>).</summary>
    public IReadOnlyList<string> ServerFeatures { get; init => field = value ?? []; } = [];

    /// <summary><see cref="PlaybackOutcomes"/>; read it before comparing sessions.</summary>
    public string Outcome { get; init => field = value ?? ""; } = "";

    /// <summary>The session's id, a canonical UUID; only on a playable decision.</summary>
    public string? SessionId { get; init; }

    public PlaybackPlan? PlaybackPlan { get; init; }

    public PlaybackTerminal? Terminal { get; init; }

    /// <summary>
    /// Protocol 3 and the features the client depends on (<c>playback_plan_v3</c>,
    /// <c>neutral_playback_v3_contract_v1</c>); anything else is a server the client cannot follow.
    /// </summary>
    [JsonIgnore]
    public bool IsCompatible =>
        ProtocolVersion == PlaybackProtocol.Version
        && PlaybackProtocol.HasFeature(ServerFeatures, PlaybackFeatures.PlaybackPlanV3)
        && PlaybackProtocol.HasFeature(ServerFeatures, PlaybackFeatures.NeutralPlaybackV3ContractV1);

    /// <summary>A playable outcome with a plan and a session to report progress to.</summary>
    [JsonIgnore]
    public bool IsPlayable => Outcome == PlaybackOutcomes.Playable && PlaybackPlan is not null && !string.IsNullOrEmpty(SessionId);
}

/// <summary>Why the server cannot play the attempt. Show <see cref="Message"/>; offer retry only when <see cref="Retryable"/>.</summary>
public sealed record PlaybackTerminal
{
    /// <summary>An open registry (planner, subtitle policy, transport); never branch on unknown values.</summary>
    public string Reason { get; init => field = value ?? ""; } = "";

    public string Message { get; init => field = value ?? ""; } = "";

    /// <summary>
    /// A new attempt may succeed. A replayed start of an ended session answers <c>session_expired</c> with this
    /// set: mint a new <c>playback_attempt_id</c>.
    /// </summary>
    public bool Retryable { get; init; }
}

/// <summary>
/// How to play the attempt (docs/api-contract.md, 8.5). Store <see cref="PlanId"/> and <see cref="PlanAttemptKey"/>
/// verbatim and echo them byte-for-byte in replans.
/// </summary>
public sealed record PlaybackPlan
{
    public int ProtocolVersion { get; init; }

    /// <summary>Opaque (<c>plan:…</c>); the replan's <c>failed_plan_id</c>.</summary>
    public string PlanId { get; init => field = value ?? ""; } = "";

    /// <summary>Opaque (<c>v3:…</c>, case-sensitive); the replan's <c>plan_attempt_key</c> and an <c>attempted_plan_keys</c> entry.</summary>
    public string PlanAttemptKey { get; init => field = value ?? ""; } = "";

    public string? SessionId { get; init; }

    /// <summary>About 24 hours after the plan was made.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary><see cref="PlaybackDeliveries"/>. Take the timeline and seek rules from <see cref="Timeline"/>, never from this.</summary>
    public string Delivery { get; init => field = value ?? ""; } = "";

    public PlaybackStream Stream { get; init => field = value ?? new(); } = new();

    public PlaybackTimeline Timeline { get; init => field = value ?? new(); } = new();

    /// <summary>The tracks this plan plays; ids refer to <see cref="EffectiveMediaFileId"/>.</summary>
    public SelectedTracks SelectedTracks { get; init => field = value ?? new(); } = new();

    public SubtitleDecision Subtitle { get; init => field = value ?? new(); } = new();

    /// <summary>What the delivered bytes are.</summary>
    public EffectiveRecipe EffectiveRecipe { get; init => field = value ?? new(); } = new();

    /// <summary>Fidelity facts: HDR kept, audio passthrough, ASS styling.</summary>
    public PlaybackPlanClaims Claims { get; init => field = value ?? new(); } = new();

    /// <summary>Server items need no action; a client-executed one the client cannot honour means rejecting the plan.</summary>
    public IReadOnlyList<PlaybackTransformation> Transformations { get; init => field = value ?? []; } = [];

    public IReadOnlyList<AppliedQuirk> AppliedQuirks { get; init => field = value ?? []; } = [];

    /// <summary>Client-side corrections the plan asks for; only with <c>device_quirks_v1</c>, which the client does not send.</summary>
    public IReadOnlyList<string> RuntimeCorrections { get; init => field = value ?? []; } = [];

    /// <summary>The quality menu in order, without the source entry ("Auto" returns to it); may be empty.</summary>
    public IReadOnlyList<AvailableQuality> AvailableQualities { get; init => field = value ?? []; } = [];

    /// <summary>The <see cref="AvailableQualities"/> label this plan delivers; absent when it delivers none of them.</summary>
    public string? ActiveQuality { get; init; }

    /// <summary>An open registry; never branch on unknown values.</summary>
    public string DecisionReason { get; init => field = value ?? ""; } = "";

    public IReadOnlyList<DegradationWarning> DegradationWarnings { get; init => field = value ?? []; } = [];

    public string RequestedMediaFileId { get; init => field = value ?? ""; } = "";

    /// <summary>The file actually played; differs from <see cref="RequestedMediaFileId"/> after an edition fallback.</summary>
    public string EffectiveMediaFileId { get; init => field = value ?? ""; } = "";

    public PlaybackSource Source { get; init => field = value ?? new(); } = new();

    public string SubtitleFidelityPolicy { get; init => field = value ?? ""; } = "";

    /// <summary>A limit the server learned from earlier rebuffering, when it applied one.</summary>
    public PlaybackBandwidthLimit? BandwidthLimit { get; init; }

    /// <summary>
    /// The plan needs work from the client the client does not do: a client-executed transformation or a
    /// runtime correction. Reject it with a <c>failure_recovery</c> replan (docs/api-contract.md, 8.12 item 9).
    /// </summary>
    [JsonIgnore]
    public bool RequiresClientExecution =>
        RuntimeCorrections.Count > 0
        || Transformations.Any(t => string.Equals(t.Executor, "client", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Where the media is (docs/api-contract.md, 9.2). <see cref="Url"/> is root-relative on the API origin: resolve it
/// with <see cref="PlaybackUrl.Resolve"/> and replay it byte-for-byte with the bearer and profile headers.
/// </summary>
public sealed record PlaybackStream
{
    public string Url { get; init => field = value ?? ""; } = "";

    /// <summary><see cref="StreamProtocols"/>.</summary>
    public string Protocol { get; init => field = value ?? ""; } = "";

    public string? Container { get; init; }

    public string? MimeType { get; init; }

    /// <summary>Extra headers for media requests; empty today.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init => field = value ?? new Dictionary<string, string>(); } =
        new Dictionary<string, string>();

    /// <summary><see cref="HeaderRefreshModes"/>; <c>none</c> today, so refresh the token out of band.</summary>
    public string HeaderRefresh { get; init => field = value ?? ""; } = "";

    public string? HeaderRefreshUrl { get; init; }
}

/// <summary>
/// How player time maps to source time (docs/api-contract.md, 8.6): <c>source = player + timeline_offset_seconds</c>.
/// Progress, stop, replan positions, markers, chapters and subtitle cues are all source time.
/// </summary>
public sealed record PlaybackTimeline
{
    public double SourceStartSeconds { get; init; }

    public double StreamOriginSeconds { get; init; }

    /// <summary>Where to start the player, in player time.</summary>
    public double PlayerStartSeconds { get; init; }

    public double TimelineOffsetSeconds { get; init; }

    /// <summary>False for a copy remux or a growing transcode: seeks outside the window need a <c>seek_reanchor</c> replan.</summary>
    public bool CanSeekAnywhere { get; init; }

    /// <summary><see cref="SeekRestorations"/>.</summary>
    public string SeekRestoration { get; init => field = value ?? ""; } = "";

    /// <summary>The start of the produced window in source time, when the stream has one.</summary>
    public double? SeekWindowStartSeconds { get; init; }

    /// <summary>The end of the produced window; absent means open-ended (still growing).</summary>
    public double? SeekWindowEndSeconds { get; init; }

    public double ToSource(double playerSeconds) => playerSeconds + TimelineOffsetSeconds;

    public double ToPlayer(double sourceSeconds) => sourceSeconds - TimelineOffsetSeconds;
}

/// <summary>Selected audio and subtitle tracks. Empty in a replan means "the server's default audio, subtitles off".</summary>
public sealed record SelectedTracks
{
    public TrackIdentity? Audio { get; init; }

    /// <summary>Absent means off.</summary>
    public TrackIdentity? Subtitle { get; init; }
}

/// <param name="Id"><c>file:{media_file_id}:{audio|subtitle}:{ordinal}</c>; for subtitles echo an inventory <c>track_id</c>.</param>
/// <param name="Index">The same ordinal; for subtitles the inventory's <c>combined_index</c>.</param>
public sealed record TrackIdentity(string Id, int? Index = null)
{
    public string Id { get; init => field = value ?? ""; } = Id ?? "";

    // Declared, not synthesized, so it is written after the id.
    public int? Index { get; init; } = Index;
}

/// <summary>What the delivered stream contains.</summary>
public sealed record EffectiveRecipe
{
    public string? VideoCodec { get; init; }

    public string? VideoSampleEntry { get; init; }

    public string? AudioCodec { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? FrameRate { get; init; }

    public int? BitrateKbps { get; init; }

    /// <summary><c>sdr</c>, <c>hdr10</c>, <c>hdr10_plus</c>, <c>hlg</c>, <c>dolby_vision</c> or <c>hdr_unknown</c>.</summary>
    public string? DynamicRange { get; init; }

    public int? AudioChannels { get; init; }

    public string? AudioLayout { get; init; }
}

public sealed record PlaybackPlanClaims
{
    public VideoClaims Video { get; init => field = value ?? new(); } = new();

    public AudioClaims Audio { get; init => field = value ?? new(); } = new();

    public SubtitleClaims Subtitles { get; init => field = value ?? new(); } = new();
}

public sealed record VideoClaims
{
    public bool Hdr10 { get; init; }

    public bool Hdr10Plus { get; init; }

    public bool Hlg { get; init; }

    public bool DolbyVision { get; init; }

    public string? DolbyVisionReason { get; init; }
}

public sealed record AudioClaims
{
    public string? Codec { get; init; }

    public bool Passthrough { get; init; }

    public bool AtmosPreserved { get; init; }

    public string? DtsVariant { get; init; }

    public string? Reason { get; init; }
}

public sealed record SubtitleClaims
{
    public bool AssStylingPreserved { get; init; }

    public bool BitmapOverlay { get; init; }

    public bool BitmapSidecar { get; init; }

    public string? Reason { get; init; }
}

/// <summary>A media transformation: who runs it (<c>server</c> or <c>client</c>), which recipe, and what it guarantees.</summary>
public sealed record PlaybackTransformation
{
    public string Name { get; init => field = value ?? ""; } = "";

    public string Executor { get; init => field = value ?? ""; } = "";

    public string RecipeVersion { get; init => field = value ?? ""; } = "";

    public IReadOnlyList<string> ValidatedClaims { get; init => field = value ?? []; } = [];
}

/// <summary>A device quirk the server applied to the plan.</summary>
public sealed record AppliedQuirk
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string RegistryRevision { get; init => field = value ?? ""; } = "";

    public string Action { get; init => field = value ?? ""; } = "";

    public string? Reason { get; init; }
}

/// <summary>A quality menu entry; pick it with a <c>quality_change</c> replan sending its <see cref="Label"/>.</summary>
public sealed record AvailableQuality
{
    public string Label { get; init => field = value ?? ""; } = "";

    /// <summary>The human-facing name, for example <c>1080p High</c>.</summary>
    public string? DisplayName { get; init; }

    public int? Height { get; init; }

    public int? BitrateKbps { get; init; }

    public bool PreservesSource { get; init; }
}

/// <param name="Code">An open registry, for example <c>audio_track_unavailable</c> or <c>subtitle_track_unavailable</c>.</param>
public sealed record DegradationWarning(string Code, string Message)
{
    public string Code { get; init => field = value ?? ""; } = Code ?? "";

    public string Message { get; init => field = value ?? ""; } = Message ?? "";
}

/// <summary>The source file as the server probed it.</summary>
public sealed record PlaybackSource
{
    public string MediaFileId { get; init => field = value ?? ""; } = "";

    /// <summary>The full runtime; absent means unknown. Never replace it with the player's duration.</summary>
    public double? DurationSeconds { get; init; }

    public string? Container { get; init; }

    public string? VideoCodec { get; init; }

    public string? VideoProfile { get; init; }

    public int? VideoLevel { get; init; }

    public int? BitDepth { get; init; }

    public string? ColorRange { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? FrameRate { get; init; }

    public int? BitrateKbps { get; init; }

    /// <summary>As <see cref="EffectiveRecipe.DynamicRange"/>.</summary>
    public string? DynamicRange { get; init; }

    public bool Hdr10Plus { get; init; }

    public int? DolbyVisionProfile { get; init; }

    public int? DolbyVisionLevel { get; init; }

    public int? DvBlCompatId { get; init; }

    public bool DvBaseLayerProven { get; init; }

    public string DvEnhancementLayer { get; init => field = value ?? ""; } = "";

    public string? AudioCodec { get; init; }

    public int? AudioChannels { get; init; }

    public string? AudioLayout { get; init; }

    public bool VideoCopyUnsafe { get; init; }
}

/// <summary>A bandwidth limit the server learned and applied to this plan.</summary>
public sealed record PlaybackBandwidthLimit
{
    public int Kbps { get; init; }

    public string Source { get; init => field = value ?? ""; } = "";

    public DateTimeOffset? ExpiresAt { get; init; }
}
