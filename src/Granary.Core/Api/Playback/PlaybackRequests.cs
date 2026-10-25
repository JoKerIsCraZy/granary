using System.Text.Json.Serialization;

namespace Granary.Core.Api.Playback;

// The bodies of the playback mutations (docs/api-contract.md 8.4, 8.7, 8.10, 10.1, 10.3). Every body is strict on
// v2: an unknown member is 422, so only documented members are modelled. Members the server accepts only with
// a client feature the client does not send (buffering_quality_v1: bandwidth_estimate_source,
// observed_throughput_kbps, learned_limit) are left out, as is the deprecated metered flag.

/// <summary>
/// <c>POST /api/v2/playback/start</c>. Mint a fresh <see cref="PlaybackAttemptId"/> per playback the user starts, and
/// reuse it only to resend an unanswered, identical start.
/// </summary>
public sealed record PlaybackStartRequest
{
    /// <summary>From <see cref="PlaybackCapabilities.InstallationId"/>.</summary>
    public required string InstallationId { get; init; }

    public int ProtocolVersion => PlaybackProtocol.Version;

    /// <summary>From <see cref="PlaybackClientReport.ClientFeatures"/>; send the same list on every replan.</summary>
    public required IReadOnlyList<string> ClientFeatures { get; init => field = value ?? []; }

    /// <summary>The chosen <see cref="WatchFileVersion.FileId"/>, unchanged.</summary>
    public required string FileId { get; init; }

    /// <summary>Must equal the <c>X-Profile-Id</c> the request is sent with.</summary>
    public required string ProfileId { get; init; }

    /// <summary><c>^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$</c>; a UUID fits.</summary>
    public required string PlaybackAttemptId { get; init; }

    /// <summary><c>auto</c>, a resolution such as <c>1080p</c>, or an <see cref="AvailableQuality.Label"/>.</summary>
    public string QualityPreference { get; init => field = value ?? QualityPreferences.Auto; } = QualityPreferences.Auto;

    /// <summary>False keeps this file through every replan of the attempt.</summary>
    public bool? AllowAlternateVersions { get; init; }

    /// <summary><see cref="SubtitleFidelity"/>.</summary>
    public string SubtitleFidelityPreference { get; init => field = value ?? SubtitleFidelity.Preserve; } = SubtitleFidelity.Preserve;

    /// <summary>
    /// Source seconds, 0 to 31,536,000. Null lets the server pick its resume point, which is 0 once the item was
    /// watched, so send the resume position explicitly; 0 means start over (docs/api-contract.md, 10.5).
    /// </summary>
    public double? StartPosition { get; init; }

    /// <summary><c>file:{file_id}:audio:{n}</c>; when sent with <see cref="AudioTrackIndex"/> both must name the same track.</summary>
    public string? AudioTrackId { get; init; }

    /// <summary>
    /// The ordinal into the version's <c>audio_tracks</c>. Always send it: the user's pick, else
    /// <see cref="WatchFileVersion.EffectiveAudioTrackIndex"/> (docs/api-contract.md, 8.8.1).
    /// </summary>
    public int? AudioTrackIndex { get; init; }

    /// <summary><c>file:{file_id}:subtitle:{n}</c>. With no subtitle member at all, subtitles are off.</summary>
    public string? SubtitleTrackId { get; init; }

    /// <summary>The combined ordinal (external tracks first), never the watch-detail stream index (docs/api-contract.md, 8.8.1).</summary>
    public int? SubtitleTrackIndex { get; init; }

    /// <summary>A real throughput measurement to this server, 100 to 1,000,000; never a guess.</summary>
    public int? BandwidthEstimateKbps { get; init; }

    /// <summary>The user's bandwidth cap, 100 to 1,000,000.</summary>
    public int? BandwidthCapKbps { get; init; }

    /// <summary>From <see cref="PlaybackClientReport.ClientCapabilities"/>.</summary>
    public required ClientCapabilities ClientCapabilities { get; init; }

    /// <summary>From <see cref="PlaybackClientReport.ClientPlaybackContext"/>.</summary>
    public required ClientPlaybackContext ClientPlaybackContext { get; init; }
}

/// <summary>
/// <c>POST /api/v2/playback/{session_id}/replan</c> (docs/api-contract.md, 8.7). Resend an unanswered replan with the
/// identical body, <see cref="ReplanRequestId"/> included.
/// </summary>
public sealed record PlaybackReplanRequest
{
    public required string InstallationId { get; init; }

    public int ProtocolVersion => PlaybackProtocol.Version;

    /// <summary>The full start-time list: v2 checks the replan body against its own list before merging.</summary>
    public required IReadOnlyList<string> ClientFeatures { get; init => field = value ?? []; }

    /// <summary><see cref="ReplanOperations"/>; always sent, although the server would assume <c>failure_recovery</c>.</summary>
    public required string Operation { get; init; }

    public required string PlaybackAttemptId { get; init; }

    /// <summary>Client-minted, 8 to 128 characters, one per replan and kept when resending it.</summary>
    public required string ReplanRequestId { get; init; }

    /// <summary>The current plan's <see cref="PlaybackPlan.PlanId"/>, verbatim.</summary>
    public required string FailedPlanId { get; init; }

    /// <summary>Client-minted, 8 to 128 characters, one per adopted plan (correlation only).</summary>
    public required string PlanAttemptId { get; init; }

    /// <summary>The current plan's <see cref="PlaybackPlan.PlanAttemptKey"/>, verbatim.</summary>
    public required string PlanAttemptKey { get; init; }

    /// <summary>
    /// At most 16 keys, verbatim. After a failure: every key tried in this chain, the failed one included. After an
    /// intent operation the history starts over.
    /// </summary>
    public IReadOnlyList<string> AttemptedPlanKeys { get; init => field = value ?? []; } = [];

    /// <summary>Plan mutations the client made locally, for example <c>transport_reopen</c>; at most 8.</summary>
    public IReadOnlyList<string>? LocalMutations { get; init; }

    /// <summary>1 to 8; the server does not stop the chain, the client does.</summary>
    public required int AttemptCount { get; init; }

    /// <summary>The current preference on every replan; for <c>quality_change</c> the picked label or <c>auto</c>.</summary>
    public string QualityPreference { get; init => field = value ?? QualityPreferences.Auto; } = QualityPreferences.Auto;

    /// <summary>Source seconds.</summary>
    public required double PositionSeconds { get; init; }

    public int? BandwidthEstimateKbps { get; init; }

    public int? BandwidthCapKbps { get; init; }

    /// <summary>The tracks the new plan should play; written as <c>{}</c> when empty. An omitted subtitle means off.</summary>
    public SelectedTracks SelectedTracks { get; init => field = value ?? new(); } = new();

    /// <summary>
    /// Required for <c>failure_recovery</c> and <c>seek_failure_recovery</c>; forbidden for <c>track_change</c>,
    /// <c>quality_change</c> and <c>output_change</c>; optional (and without effect) for <c>seek_reanchor</c>.
    /// </summary>
    public PlaybackFailure? Failure { get; init; }

    public required ClientCapabilities ClientCapabilities { get; init; }

    public required ClientPlaybackContext ClientPlaybackContext { get; init; }
}

/// <summary>Why a plan failed. <see cref="Classification"/> is free-form unless it is one of <see cref="FailureClassifications"/>.</summary>
public sealed record PlaybackFailure
{
    /// <summary>At most 64 characters.</summary>
    public required string Classification { get; init; }

    /// <summary>At most 512 characters.</summary>
    public string? Message { get; init; }

    /// <summary>At most 128 characters.</summary>
    public string? DecoderName { get; init; }
}

/// <summary>A sequenced position report: the attempt's progress and the server's receipt of it (docs/api-contract.md, 10.1).</summary>
/// <param name="Sequence">1 or more, increasing per session (across its replans); resend a sample with the same one.</param>
/// <param name="Position">Source seconds, finite and not negative.</param>
/// <param name="IsPaused">The only way the server learns about a pause; a paused session stays alive for 30 minutes.</param>
public sealed record PlaybackSample(long Sequence, double Position, bool IsPaused);

/// <summary><c>POST /api/v2/playback/{session_id}/progress</c>, every 10 s while the session is open, playing or paused.</summary>
public sealed record PlaybackProgressRequest
{
    public required string InstallationId { get; init; }

    public required long Sequence { get; init; }

    public required double Position { get; init; }

    public required bool IsPaused { get; init; }

    public static PlaybackProgressRequest For(string installationId, PlaybackSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return new PlaybackProgressRequest { InstallationId = installationId, Sequence = sample.Sequence, Position = sample.Position, IsPaused = sample.IsPaused };
    }
}

/// <summary>
/// <c>DELETE /api/v2/playback/{session_id}</c> with a JSON body (docs/api-contract.md, 10.3). The first stop wins; every
/// later one answers the stored receipt.
/// </summary>
public sealed record PlaybackStopRequest
{
    public required string InstallationId { get; init; }

    /// <summary>A canonical UUID minted once per stop, persisted, and reused on every retry.</summary>
    public required string StopId { get; init; }

    /// <summary>
    /// The last sample, applied only when its sequence is newer than the last progress: send it as last sequence + 1.
    /// Its members go on the wire together or not at all, as the server requires.
    /// </summary>
    [JsonIgnore]
    public PlaybackSample? FinalSample { get; init; }

    public long? Sequence => FinalSample?.Sequence;

    public double? Position => FinalSample?.Position;

    public bool? IsPaused => FinalSample?.IsPaused;
}

/// <summary>
/// <c>POST /api/v2/playback/route-events</c> (docs/api-contract.md, 8.10): diagnostics only. On 429 or 403 drop the
/// event; never retry it automatically.
/// </summary>
public sealed record PlaybackRouteEvent
{
    public required string InstallationId { get; init; }

    /// <summary>A client-minted canonical UUID; a retry with the same id after a lost answer is recorded once.</summary>
    public required string EventId { get; init; }

    public int ProtocolVersion => PlaybackProtocol.Version;

    public required string PlaybackAttemptId { get; init; }

    /// <summary>Left out for a terminal at start, as are the plan ids.</summary>
    public string? SessionId { get; init; }

    public string? PlanId { get; init; }

    public string? PlanAttemptId { get; init; }

    public string? PlanAttemptKey { get; init; }

    /// <summary><see cref="RouteEvents"/>.</summary>
    public required string Event { get; init; }

    /// <summary>At most 64 characters.</summary>
    public string? FailureClassification { get; init; }

    /// <summary>At most 64 characters.</summary>
    public string? FallbackReason { get; init; }

    public string? OutputContextId { get; init; }

    /// <summary>At most 32 entries (<see cref="RouteDiagnostics"/>); the server keeps only the keys it knows. Written as <c>{}</c> when empty.</summary>
    public IReadOnlyDictionary<string, string> Diagnostics { get; init => field = value ?? new Dictionary<string, string>(); } =
        new Dictionary<string, string>();
}

/// <summary>The receipt of a progress sample or a stop (one shape for both, as the server answers).</summary>
public sealed record PlaybackMutation
{
    /// <summary><see cref="PlaybackMutationOutcomes"/>.</summary>
    public string Outcome { get; init => field = value ?? ""; } = "";

    /// <summary>The newest committed sample; after a stale sample, the newer one. Continue from max(own, this) sequence.</summary>
    public PlaybackSample? Accepted { get; init; }

    /// <summary>Stop only: the stop id of the stop that won.</summary>
    public string? StopId { get; init; }

    /// <summary>Stop only: the watch-history entry it wrote.</summary>
    public string? HistoryId { get; init; }
}

/// <summary>The 202 of a route event: queued for recording, not proof of a durable write.</summary>
public sealed record PlaybackRouteEventReceipt
{
    public string EventId { get; init => field = value ?? ""; } = "";

    /// <summary><c>accepted</c>.</summary>
    public string Outcome { get; init => field = value ?? ""; } = "";
}
