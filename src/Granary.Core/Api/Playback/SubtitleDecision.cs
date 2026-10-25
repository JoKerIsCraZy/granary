using System.Text.Json.Serialization;

namespace Granary.Core.Api.Playback;

/// <summary>
/// The plan's subtitle choice and the authoritative track list (docs/api-contract.md, 8.8 and 9.6). Select tracks
/// only by echoing an inventory <see cref="SubtitleInventoryItem.TrackId"/> or <see cref="SubtitleInventoryItem.CombinedIndex"/>;
/// never count entries.
/// </summary>
public sealed record SubtitleDecision
{
    /// <summary><see cref="SubtitleModes"/>.</summary>
    public string Mode { get; init => field = value ?? ""; } = "";

    /// <summary>The selected inventory entry; absent when <see cref="Mode"/> is <c>off</c>.</summary>
    public string? TrackId { get; init; }

    /// <summary>The sidecar to fetch under <c>render</c> or <c>convert</c>; cues are source time.</summary>
    public SubtitleArtifact? Artifact { get; init; }

    /// <summary>Direct play only: the stream in the original file to select.</summary>
    public EmbeddedSubtitle? Embedded { get; init; }

    /// <summary>Only with <c>hls_subtitle_renditions_v1</c>, which the client does not send.</summary>
    public HlsSubtitleRendition? HlsRendition { get; init; }

    /// <summary>External, then embedded in container order, then downloaded; gap-free combined ordinals.</summary>
    public IReadOnlyList<SubtitleInventoryItem> Inventory { get; init => field = value ?? []; } = [];

    /// <summary>The inventory entry of <see cref="TrackId"/>, or null.</summary>
    [JsonIgnore]
    public SubtitleInventoryItem? SelectedTrack => TrackId is null ? null : Inventory.FirstOrDefault(i => i.TrackId == TrackId);
}

/// <summary>One subtitle track of the effective file.</summary>
public sealed record SubtitleInventoryItem
{
    /// <summary><c>file:{media_file_id}:subtitle:{combined_index}</c>.</summary>
    public string TrackId { get; init => field = value ?? ""; } = "";

    public int CombinedIndex { get; init; }

    /// <summary><see cref="SubtitleSources"/>.</summary>
    public string Source { get; init => field = value ?? ""; } = "";

    public string? Codec { get; init; }

    public string? Language { get; init; }

    public string? Label { get; init; }

    public bool Forced { get; init; }

    public bool Default { get; init; }

    public bool HearingImpaired { get; init; }

    /// <summary><see cref="SubtitleDeliveries"/>.</summary>
    public string Delivery { get; init => field = value ?? ""; } = "";

    /// <summary>
    /// A sidecar's session URL (<c>.vtt</c>, <c>.ass</c>, <c>.srt</c>, <c>.sup</c>), present in every mode. Use it
    /// verbatim; only a <c>.vtt</c> URL may gain <c>timestamp_offset</c>.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>For embedded ASS/SSA: a JSON operation answering the fonts as base64 items.</summary>
    public string? FontBundleUrl { get; init; }

    /// <summary>Names an external or downloaded track to the subtitle sync operations.</summary>
    public string? SyncKey { get; init; }
}

/// <summary>A sidecar subtitle file.</summary>
public sealed record SubtitleArtifact
{
    public string Url { get; init => field = value ?? ""; } = "";

    public string MimeType { get; init => field = value ?? ""; } = "";

    public string Format { get; init => field = value ?? ""; } = "";

    /// <summary>0: cue times are absolute source time; subtract the timeline offset to place them.</summary>
    public double TimingOriginSeconds { get; init; }
}

/// <summary>An embedded subtitle stream to select in the original file.</summary>
public sealed record EmbeddedSubtitle
{
    /// <summary>The FFmpeg stream index (mpv <c>track-list/N/ff-index</c>).</summary>
    public int StreamIndex { get; init; }

    public string? ContainerTrackId { get; init; }
}

/// <summary>A WebVTT subtitle rendition inside the plan's HLS stream.</summary>
public sealed record HlsSubtitleRendition
{
    public string GroupId { get; init => field = value ?? ""; } = "";

    public string Name { get; init => field = value ?? ""; } = "";

    public string Format { get; init => field = value ?? ""; } = "";

    public string MimeType { get; init => field = value ?? ""; } = "";
}
