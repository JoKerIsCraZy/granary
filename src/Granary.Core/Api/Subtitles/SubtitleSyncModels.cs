using System.Text.Json.Serialization;

namespace Granary.Core.Api.Subtitles;

/// <summary>What the server says about subtitle sync (<c>getSubtitleSyncStatus</c>).</summary>
public sealed record SubtitleSyncStatus
{
    /// <summary><c>available</c>, <c>disabled</c>, <c>not_configured</c> or <c>unsupported</c>.</summary>
    public string State { get; init => field = value ?? ""; } = "";

    /// <summary>This viewer may use it.</summary>
    public bool Allowed { get; init; }

    /// <summary>New subtitles are synced as they are stored.</summary>
    public bool AutoSync { get; init; }

    /// <summary>Subtitle files next to the media can be synced too, not only stored ones.</summary>
    public bool External { get; init; }

    /// <summary>Sync is offered: available and allowed, as Silo's Apple app reads it.</summary>
    [JsonIgnore]
    public bool IsAvailable => Allowed && State == "available";
}

/// <summary>
/// A subtitle's timing correction: original time t plays at t × <see cref="Scale"/> + <see cref="OffsetMs"/>. The
/// server applies it on every delivery path.
/// </summary>
public sealed record SubtitleTiming
{
    public static SubtitleTiming Identity { get; } = new() { OffsetMs = 0, RawScale = 1 };

    public long OffsetMs { get; init; }

    // A scale missing from the JSON is no stretch, not zero (the source generator would leave an initializer out).
    [JsonPropertyName("scale")]
    public double? RawScale { get; init; }

    [JsonIgnore]
    public double Scale => RawScale ?? 1;

    /// <summary>The original timing: no shift, no stretch.</summary>
    [JsonIgnore]
    public bool IsIdentity => OffsetMs == 0 && Scale == 1;

    /// <summary>The same correction (record equality would tell a missing scale from 1).</summary>
    public bool SameAs(SubtitleTiming? other) => other is null ? IsIdentity : OffsetMs == other.OffsetMs && Scale == other.Scale;
}

/// <summary>The states of a sync job (<see cref="SubtitleSyncJob.Status"/>).</summary>
public static class SubtitleSyncStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Synced = "synced";
    public const string AlreadySynced = "already_synced";
    public const string NoMatch = "no_match";
    public const string Failed = "failed";

    /// <summary>Queued or running: the job has not ended.</summary>
    public static bool InProgress(string? status) => status is Pending or Running;
}

/// <summary>A subtitle's sync job: queued, running, then how it ended.</summary>
public sealed record SubtitleSyncJob
{
    public string Id { get; init => field = value ?? ""; } = "";

    /// <summary><see cref="SubtitleSyncStatuses"/>.</summary>
    public string Status { get; init => field = value ?? ""; } = "";

    /// <summary><c>auto</c> (a subtitle synced as it was stored) or <c>manual</c>.</summary>
    public string Trigger { get; init => field = value ?? ""; } = "";

    /// <summary><c>queued</c>, <c>analyzing</c> or <c>matching</c> while it runs.</summary>
    public string? Phase { get; init; }

    /// <summary>0 to 1 while it runs.</summary>
    public double? Progress { get; init; }

    /// <summary>Why a failed job failed: <c>subtitle_changed</c>, <c>no_audio</c>, <c>unavailable</c> or <c>error</c>.</summary>
    public string? Failure { get; init; }

    public double? Confidence { get; init; }

    /// <summary>The correction a synced job found.</summary>
    public SubtitleTiming? Result { get; init; }

    /// <summary>RFC 3339 in UTC with milliseconds: ordinal order is time order.</summary>
    public string CreatedAt { get; init => field = value ?? ""; } = "";

    public string? FinishedAt { get; init; }

    [JsonIgnore]
    public bool IsInProgress => SubtitleSyncStatuses.InProgress(Status);
}

/// <summary>
/// One subtitle the server can sync, by its sync key (<c>stored-{id}</c> or <c>external-{sha256}</c>), as the plan's
/// inventory names it in <c>sync_key</c>.
/// </summary>
public sealed record SubtitleSyncState
{
    public string Key { get; init => field = value ?? ""; } = "";

    public string MediaFileId { get; init => field = value ?? ""; } = "";

    /// <summary><c>downloaded</c> or <c>external</c> (a file next to the media, which the server never changes).</summary>
    public string Source { get; init => field = value ?? ""; } = "";

    public string? StoredSubtitleId { get; init; }

    public string Language { get; init => field = value ?? ""; } = "";

    public string Format { get; init => field = value ?? ""; } = "";

    public string Label { get; init => field = value ?? ""; } = "";

    public SubtitleTiming Timing { get; init => field = value ?? SubtitleTiming.Identity; } = SubtitleTiming.Identity;

    /// <summary>The latest job; null when it never had one.</summary>
    public SubtitleSyncJob? Sync { get; init; }

    [JsonIgnore]
    public bool IsExternal => Source == "external";
}

internal sealed record SubtitleSyncList
{
    public IReadOnlyList<SubtitleSyncState> Subtitles { get; init => field = value ?? []; } = [];
}

internal sealed record SubtitleSyncEnvelope
{
    public SubtitleSyncState Subtitle { get; init => field = value ?? new SubtitleSyncState(); } = new();
}

internal sealed record SubtitleTimingBody
{
    public long OffsetMs { get; init; }

    public double Scale { get; init; }
}
