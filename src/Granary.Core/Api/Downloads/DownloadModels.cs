using Granary.Core.Api.Playback;

namespace Granary.Core.Api.Downloads;

/// <summary>
/// <c>GET /api/v2/capabilities/downloads</c> for the acting profile (downloads-api.md, 3). Download actions show only when it
/// <see cref="IsUsable"/>; downloads already on the PC stay playable whatever it says.
/// </summary>
public sealed record DownloadCapability
{
    /// <summary>The server has downloads turned on.</summary>
    public bool Enabled { get; init; }

    /// <summary>This user may download, demo mode aside.</summary>
    public bool Allowed { get; init; }

    /// <summary><c>/file-proxy</c> may answer 307 to a proxy node.</summary>
    public bool ProxyDelivery { get; init; }

    /// <summary>Listed preparing entries carry <see cref="DownloadEntry.Preparation"/>.</summary>
    public bool PreparationProgress { get; init; }

    /// <summary>A finished download's expired prepared file can be prepared again (<c>/prepare</c>).</summary>
    public bool PrepareAgain { get; init; }

    /// <summary>A <c>revoked</c> entry means: delete the local copy, then the entry.</summary>
    public bool RevokedRemovesLocal { get; init; }

    /// <summary>The presets this user may ask for now, best first; empty when downloads are not available.</summary>
    public IReadOnlyList<string> QualityPresets { get; init => field = value ?? []; } = [];

    /// <summary>One per preset, in the same order, with its bitrate and tallest output for a label.</summary>
    public IReadOnlyList<DownloadQualityOption> QualityOptions { get; init => field = value ?? []; } = [];

    public bool IsUsable => Enabled && Allowed;
}

/// <summary>A preset's video bitrate cap and tallest output on this server; both are absent for <c>original</c>.</summary>
public sealed record DownloadQualityOption(string Preset, int? BitrateKbps, int? MaxHeight);

/// <summary>
/// The <c>caps</c> of a create (downloads-api.md, 4.1): what this PC decodes, so an <c>original</c> the PC plays comes as it
/// is and anything else as a remux or a compatible copy. Every member is required once <c>caps</c> is sent.
/// </summary>
public sealed record DownloadCaps(IReadOnlyList<string> CodecsVideo, IReadOnlyList<string> CodecsAudio, IReadOnlyList<string> Containers,
    string MaxResolution, bool Hdr)
{
    /// <summary>
    /// The lists of Granary's playback report (<see cref="PlaybackReportBuilder"/>) for this PC. The ceiling is 2160p, since
    /// mpv decodes 4K and scales it to the window; HDR is false, since Granary's output is SDR, as its playback report says.
    /// </summary>
    public static DownloadCaps For(PlaybackDeviceFacts facts, bool softwareDecoding)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var report = PlaybackReportBuilder.Build(facts, "0", softwareDecoding: softwareDecoding);
        return new DownloadCaps(report.ClientCapabilities.CodecsVideo, PlaybackReportBuilder.AudioCodecs, PlaybackReportBuilder.Containers,
            MaxResolution: "2160p", Hdr: false);
    }
}

/// <summary>
/// <c>POST /api/v2/downloads</c> for one film or episode. An episode's <see cref="ContentId"/> is its series and
/// <see cref="EpisodeId"/> the episode. <see cref="ExpectedRevision"/> 0 asks for a new entry.
/// </summary>
public sealed record CreateDownloadBody
{
    public required string ContentId { get; init; }

    public string? EpisodeId { get; init; }

    /// <summary>The version the viewer chose; left out, the server picks one from the profile's history.</summary>
    public string? MediaFileId { get; init; }

    public required string Quality { get; init; }

    public DownloadCaps? Caps { get; init; }

    public long? ExpectedRevision { get; init; }

    /// <summary>The entry a positive <see cref="ExpectedRevision"/> reuses or replaces.</summary>
    public string? ExpectedDownloadId { get; init; }
}

/// <summary>A managed registry entry (downloads-api.md, 5). Sizes are bytes, over 4 GB for any 4K original.</summary>
public sealed record DownloadEntry
{
    public required string Id { get; init; }

    /// <summary>The film, or an episode's series.</summary>
    public required string ContentId { get; init; }

    public string? EpisodeId { get; init; }

    public string? MediaFileId { get; init; }

    /// <summary><c>original</c>, <c>remux</c> or <c>transcode</c>.</summary>
    public string? DeliveryFormat { get; init; }

    /// <summary>May be an estimate while the entry prepares.</summary>
    public long FileSize { get; init; }

    /// <summary>Goes up when the entry is replaced with new bytes; status reports carry it.</summary>
    public long Revision { get; init; }

    /// <summary>One of <see cref="DownloadStatuses"/>.</summary>
    public required string Status { get; init; }

    /// <summary>The preset asked for.</summary>
    public required string Quality { get; init; }

    /// <summary>Where a preparing entry's file is, on listed entries when the capability has <c>preparation_progress</c>.</summary>
    public DownloadPreparation? Preparation { get; init; }
}

/// <summary>A preparing entry's place in the server's queue (<c>queued</c>) or its encode's progress, 0 to 1 (<c>running</c>).</summary>
public sealed record DownloadPreparation(string? State, int? QueuePosition, double? Progress, int? RemainingSeconds);

/// <summary>What a create answers: the entries made or reused, and the episodes it passed over.</summary>
public sealed record DownloadCreated(IReadOnlyList<DownloadEntry> Items, IReadOnlyList<DownloadSkipped> Skipped)
{
    public IReadOnlyList<DownloadEntry> Items { get; init; } = Items ?? [];

    public IReadOnlyList<DownloadSkipped> Skipped { get; init; } = Skipped ?? [];
}

/// <summary>An episode a create passed over, with why (<c>no_file</c>, <c>quality_unavailable</c>).</summary>
public sealed record DownloadSkipped(string? EpisodeId, string? Reason);

/// <summary>One page of <c>GET /api/v2/downloads</c>, newest first.</summary>
public sealed record DownloadEntryPage(IReadOnlyList<DownloadEntry> Items, DownloadPageInfo? Page)
{
    public IReadOnlyList<DownloadEntry> Items { get; init; } = Items ?? [];
}

/// <summary>Whether more pages follow, and the cursor that reads the next one.</summary>
public sealed record DownloadPageInfo(bool HasMore, string? NextCursor);

/// <summary>
/// <c>PATCH /api/v2/downloads/{id}</c>: what the PC holds now (<c>downloading</c> or <c>completed</c>) for one revision's bytes.
/// The time is taken when the state changes and kept for a retry, so the server orders the reports as they happened.
/// </summary>
public sealed record DownloadStatusEvent(string Status, DateTimeOffset UpdatedAt, long Revision);

/// <summary>The lifecycle states of an entry, as the server writes them.</summary>
public static class DownloadStatuses
{
    public const string Preparing = "preparing";
    public const string Ready = "ready";
    public const string Downloading = "downloading";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Revoked = "revoked";
    public const string Cancelled = "cancelled";
}

/// <summary>The quality presets, best first (downloads-api.md, 1).</summary>
public static class DownloadQualities
{
    public const string Original = "original";

    public static IReadOnlyList<string> Ladder { get; } = [Original, "20mbps", "10mbps", "5mbps", "2mbps", "1mbps"];
}
