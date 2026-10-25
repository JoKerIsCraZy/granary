namespace Granary.Core.Api.Playback;

// What the client reports at start and on every replan (docs/api-contract.md, 8.4). These bodies are strict:
// only documented members are modelled, required ones are always written (lists as [], objects as {}), and
// optional ones are left out when null. HDR details, audio passthrough and per-decoder video_decode entries
// are not modelled yet: the client declares SDR output and decodes all audio itself.

/// <summary><c>client_capabilities</c>: what the device decodes, at an honest <see cref="CapabilityEvidence"/> tier.</summary>
public sealed record ClientCapabilities
{
    public required string VideoEvidence { get; init; }

    public required string AudioEvidence { get; init; }

    /// <summary>Every video codec the player decodes smoothly, lower-case ffprobe names (<c>h264</c>, <c>hevc</c>, …).</summary>
    public IReadOnlyList<string> CodecsVideo { get; init => field = value ?? []; } = [];

    /// <summary>The codecs with a hardware decoder on this machine.</summary>
    public IReadOnlyList<string> CodecsVideoHardware { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> CodecsAudio { get; init => field = value ?? []; } = [];

    /// <summary>Containers as the server's scanner names them (<c>mkv</c>, <c>mp4</c>, <c>ts</c>, …); direct play needs the source's.</summary>
    public IReadOnlyList<string> Containers { get; init => field = value ?? []; } = [];

    /// <summary>
    /// A decoder ceiling (<c>"&lt;N&gt;p"</c> or <c>4k</c>); never the monitor's size, which would turn a 4K
    /// direct play into a transcode (docs/api-contract.md, 13.2). Omitted means no limit.
    /// </summary>
    public string? MaxResolution { get; init; }

    public bool Hdr { get; init; }
}

/// <summary><c>client_playback_context</c>: the app, device, output and the delivery classes it can execute.</summary>
public sealed record ClientPlaybackContext
{
    public int ProtocolVersion => PlaybackProtocol.Version;

    /// <summary>For example <c>desktop</c>; at most 32 characters.</summary>
    public required string FormFactor { get; init; }

    public required string AppVersion { get; init; }

    public string? AppBuild { get; init; }

    public string? AppChannel { get; init; }

    /// <summary>Required even when empty: then it is written as <c>{}</c>, never left out or null.</summary>
    public DeviceContext Device { get; init => field = value ?? new(); } = new();

    /// <summary>Required even when empty, like <see cref="Device"/>.</summary>
    public OutputContext Output { get; init => field = value ?? new(); } = new();

    /// <summary>Keyed by <see cref="DeliveryClasses"/>. A class left out is unavailable.</summary>
    public IReadOnlyDictionary<string, DeliveryCapability> Deliveries { get; init => field = value ?? new Dictionary<string, DeliveryCapability>(); } =
        new Dictionary<string, DeliveryCapability>();
}

/// <summary>Platform-neutral device identity; the server matches device quirks on manufacturer and model.</summary>
public sealed record DeviceContext
{
    /// <summary>At most 32 characters, for example <c>windows</c>.</summary>
    public string? Platform { get; init; }

    /// <summary>At most 128 characters, as are <see cref="Manufacturer"/> and <see cref="Model"/>.</summary>
    public string? OsVersion { get; init; }

    public string? Manufacturer { get; init; }

    public string? Model { get; init; }
}

/// <summary>
/// The active output. Empty while the client renders SDR only and mixes audio itself: HDR display facts
/// (<c>display</c>, <c>hdr_details</c>) and passthrough arrive with HDR output support.
/// </summary>
public sealed record OutputContext;

/// <summary>
/// One delivery class the client can execute. An empty list means "unconstrained" (the top-level
/// <see cref="ClientCapabilities"/> still apply); a non-empty one narrows them for this class.
/// </summary>
public sealed record DeliveryCapability
{
    public required bool Enabled { get; init; }

    public required bool SupportedOnDevice { get; init; }

    /// <summary>Why the class does not work on this device, when <see cref="SupportedOnDevice"/> is false.</summary>
    public string? FailureReason { get; init; }

    public IReadOnlyList<string> Containers { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> VideoCodecs { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> AudioDecodeCodecs { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> AudioPassthroughCodecs { get; init => field = value ?? []; } = [];

    /// <summary>The most audio channels the class can play; leave it out when the player downmixes itself.</summary>
    public int? MaxChannels { get; init; }

    public DeliverySubtitleCapabilities Subtitles { get; init => field = value ?? new(); } = new();

    public IReadOnlyList<string> Features { get; init => field = value ?? []; } = [];

    /// <summary>Ignored by servers today; media credentials are refreshed out of band.</summary>
    public bool AuthHeaderRefresh { get; init; }

    /// <summary><see cref="DeliveryClaims"/>.</summary>
    public IReadOnlyList<string> ValidatedClaims { get; init => field = value ?? []; } = [];

    /// <summary>Client-executed transformations; only with <c>client_video_transformations_v1</c>, else the body is 422.</summary>
    public IReadOnlyList<PlaybackTransformation> Transformations { get; init => field = value ?? []; } = [];
}

/// <summary>How the class can show subtitles (docs/api-contract.md, 8.4 and 8.8).</summary>
public sealed record DeliverySubtitleCapabilities
{
    /// <summary>
    /// Native selection of embedded tracks in the original file; only on <c>original_http</c> and only with
    /// <c>embedded_subtitles_v1</c>. Left out when null.
    /// </summary>
    public IReadOnlyList<NativeEmbeddedSubtitleCapability>? NativeEmbedded { get; init; }

    public bool EmbeddedText { get; init; }

    public bool SidecarText { get; init; }

    public bool AssStyling { get; init; }

    /// <summary>Embedded bitmap tracks (PGS arrives as a <c>.sup</c> sidecar); DVD/DVB bitmaps are burned in unless native.</summary>
    public bool EmbeddedBitmap { get; init; }

    /// <summary>External bitmap subtitle files; the server refuses them.</summary>
    public bool SidecarBitmap { get; init; }

    public bool FontAttachments { get; init; }
}

/// <summary>Native selection of embedded subtitles in one container (every member is required).</summary>
public sealed record NativeEmbeddedSubtitleCapability
{
    /// <summary>As the scanner names it, for example <c>mkv</c>.</summary>
    public required string Container { get; init; }

    /// <summary>1 to 32 FFmpeg codec names, for example <c>subrip</c>, <c>hdmv_pgs_subtitle</c>.</summary>
    public required IReadOnlyList<string> Codecs { get; init; }

    /// <summary><see cref="SubtitleTrackIdentities"/>.</summary>
    public required string TrackIdentity { get; init; }

    public bool AssStyling { get; init; }

    public bool FontAttachments { get; init; }
}
