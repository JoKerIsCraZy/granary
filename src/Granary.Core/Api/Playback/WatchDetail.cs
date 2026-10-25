using System.Text.Json.Serialization;

namespace Granary.Core.Api.Playback;

/// <summary>
/// <c>GET /api/v2/watch/{id}</c>: what the client needs right before playback of a movie or episode
/// (docs/api-contract.md, 7.8): the files to choose from, the resume point, the saved track preferences
/// and, per file, markers and chapters. Image members are presigned and may be root-relative: resolve them
/// with <see cref="Images.ArtworkUrl.Resolve"/>.
/// </summary>
public sealed record WatchDetail
{
    public string ContentId { get; init => field = value ?? ""; } = "";

    /// <summary>Open set, see <see cref="WatchItemTypes"/>.</summary>
    public string Type { get; init => field = value ?? ""; } = "";

    /// <summary>The movie's title, or for an episode the episode's own title (the show is <see cref="SeriesTitle"/>).</summary>
    public string Title { get; init => field = value ?? ""; } = "";

    public int? Year { get; init; }

    public string? Overview { get; init; }

    /// <summary>The owning series of an episode.</summary>
    public string? SeriesId { get; init; }

    public string? SeriesTitle { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    /// <summary>For the system media controls: the movie's poster or the episode's still.</summary>
    public string? PosterUrl { get; init; }

    public string? PosterThumbhash { get; init; }

    /// <summary>The item's backdrop; for an episode the series backdrop.</summary>
    public string? BackdropUrl { get; init; }

    public string? BackdropThumbhash { get; init; }

    /// <summary>The profile's progress; absent without a profile or without progress.</summary>
    public WatchUserData? UserData { get; init; }

    /// <summary>Every playable file of the item. Each part of a multi-part item is also one of these.</summary>
    public IReadOnlyList<WatchFileVersion> Versions { get; init => field = value ?? []; } = [];

    /// <summary>Logical watch choices, each spanning one or more ordered parts; empty when the server sends none.</summary>
    public IReadOnlyList<WatchPlaybackVariant> PlaybackVariants { get; init => field = value ?? []; } = [];

    /// <summary>The item's subtitles across its files, for display only; start selects by the version's tracks.</summary>
    public IReadOnlyList<WatchSubtitle> Subtitles { get; init => field = value ?? []; } = [];

    /// <summary>
    /// Item-level markers mirroring one file the server picked. Use the playing version's own markers
    /// (<see cref="WatchFileVersion.MarkerSegments"/>) instead (docs/api-contract.md, 11.1).
    /// </summary>
    public WatchMarker? Intro { get; init; }

    public WatchMarker? Credits { get; init; }

    public WatchMarker? Recap { get; init; }

    public WatchMarker? Preview { get; init; }

    /// <summary>The subtitle language the profile's preferences resolve to (BCP 47); empty when the preference is None.</summary>
    public string? EffectiveSubtitleLanguage { get; init; }

    /// <summary>
    /// <see cref="SubtitlePreferenceModes"/>; omitted when it is the default <c>auto</c>, so read
    /// <see cref="SubtitleMode"/> rather than treating null as unknown.
    /// </summary>
    public string? EffectiveSubtitleMode { get; init; }

    /// <summary>Omitted when it is the default <c>true</c>; read <see cref="ShowForcedSubtitles"/>.</summary>
    public bool? EffectiveShowForcedSubtitles { get; init; }

    /// <summary>The subtitle track the profile last chose on this item; match it before any language rule.</summary>
    public WatchSubtitleSignature? EffectiveSubtitleTrackSignature { get; init; }

    /// <summary>The version the profile last played: its resolution, HDR flag, video codec and edition.</summary>
    public string? EffectiveVersionResolution { get; init; }

    public bool? EffectiveVersionHdr { get; init; }

    public string? EffectiveVersionCodecVideo { get; init; }

    public string? EffectiveVersionEditionKey { get; init; }

    /// <summary>The resolved subtitle mode: <see cref="EffectiveSubtitleMode"/>, or <c>auto</c> when the server left it out.</summary>
    [JsonIgnore]
    public string SubtitleMode => string.IsNullOrEmpty(EffectiveSubtitleMode) ? SubtitlePreferenceModes.Auto : EffectiveSubtitleMode;

    /// <summary>The resolved show-forced flag: <see cref="EffectiveShowForcedSubtitles"/>, or true when the server left it out.</summary>
    [JsonIgnore]
    public bool ShowForcedSubtitles => EffectiveShowForcedSubtitles ?? true;

    /// <summary>The file with <paramref name="fileId"/> among <see cref="Versions"/> and every variant's parts, or null.</summary>
    public WatchFileVersion? FindVersion(string fileId) =>
        Versions.FirstOrDefault(v => v.FileId == fileId)
        ?? PlaybackVariants.SelectMany(v => v.Parts).SelectMany(p => p.Versions).FirstOrDefault(v => v.FileId == fileId);
}

/// <summary>The profile's progress on the item (docs/api-contract.md, 10.5). Positions are source seconds.</summary>
public sealed record WatchUserData
{
    /// <summary>The resume point; 0 when there is none (the server leaves it out).</summary>
    public double PositionSeconds { get; init; }

    public double DurationSeconds { get; init; }

    public bool IsInProgress { get; init; }

    /// <summary>
    /// The watched flag. It stays set through a rewatch, so an omitted <c>start_position</c> can begin at 0
    /// although <see cref="PositionSeconds"/> is set: send the position explicitly to resume.
    /// </summary>
    public bool Played { get; init; }

    public int WatchedCount { get; init; }

    public int UnplayedCount { get; init; }

    public int InProgressCount { get; init; }

    /// <summary>The version last played.</summary>
    public string? LastFileId { get; init; }

    public string? LastResolution { get; init; }

    public bool? LastHdr { get; init; }

    public string? LastCodecVideo { get; init; }

    public string? LastEditionKey { get; init; }
}

/// <summary>One playable file of the item. Its <see cref="FileId"/> is the start request's <c>file_id</c>, sent back unchanged.</summary>
public sealed record WatchFileVersion
{
    public string FileId { get; init => field = value ?? ""; } = "";

    public string? FileName { get; init; }

    /// <summary>Only for accounts allowed to see paths.</summary>
    public string? FilePath { get; init; }

    public string Resolution { get; init => field = value ?? ""; } = "";

    public string CodecVideo { get; init => field = value ?? ""; } = "";

    public string CodecAudio { get; init => field = value ?? ""; } = "";

    public bool Hdr { get; init; }

    public string Container { get; init => field = value ?? ""; } = "";

    /// <summary>Bytes.</summary>
    public long FileSize { get; init; }

    public long DurationSeconds { get; init; }

    /// <summary>Kilobits per second, although the schema says bits (docs/api-contract.md, 7.9).</summary>
    public long Bitrate { get; init; }

    public DateTimeOffset? AddedAt { get; init; }

    public string? EditionKey { get; init; }

    /// <summary>The edition as the file names it, for example <c>Extended Cut</c>.</summary>
    public string? EditionRaw { get; init; }

    public string? PresentationKind { get; init; }

    public string? PresentationGroupKey { get; init; }

    /// <summary>This file's part within its presentation, counted from 0.</summary>
    public int PresentationPartIndex { get; init; }

    /// <summary>The parts of the presentation; 0 or 1 for a whole file.</summary>
    public int PresentationPartTotal { get; init; }

    public int? MultiEpisodeStart { get; init; }

    public int? MultiEpisodeEnd { get; init; }

    /// <summary>
    /// The audio track the profile's preferences pick, as an ordinal into <see cref="AudioTracks"/>. Send it as
    /// <c>audio_track_index</c> unless the user chose another (docs/api-contract.md, 8.8.1).
    /// </summary>
    public int? EffectiveAudioTrackIndex { get; init; }

    public string? EffectiveAudioLanguage { get; init; }

    public IReadOnlyList<WatchVideoTrack> VideoTracks { get; init => field = value ?? []; } = [];

    /// <summary>In stream order; a track's position here is its audio ordinal (<c>file:{id}:audio:{n}</c>).</summary>
    public IReadOnlyList<WatchAudioTrack> AudioTracks { get; init => field = value ?? []; } = [];

    /// <summary>
    /// Embedded tracks first. Their <see cref="WatchSubtitleTrack.Index"/> is the FFmpeg stream index, not the
    /// start request's combined ordinal, which counts external tracks first (docs/api-contract.md, 8.8.1).
    /// </summary>
    public IReadOnlyList<WatchSubtitleTrack> SubtitleTracks { get; init => field = value ?? []; } = [];

    public IReadOnlyList<WatchChapter> Chapters { get; init => field = value ?? []; } = [];

    /// <summary>Every marker occurrence of this file in source-time order; a kind may repeat. Ranges are <c>[start, end)</c>.</summary>
    public IReadOnlyList<WatchMarkerSegment> MarkerSegments { get; init => field = value ?? []; } = [];

    /// <summary>Single-range compatibility projections of <see cref="MarkerSegments"/>.</summary>
    public WatchMarker? Intro { get; init; }

    public WatchMarker? Credits { get; init; }

    public WatchMarker? Recap { get; init; }

    public WatchMarker? Preview { get; init; }

    public bool TrickplayAvailable { get; init; }

    /// <summary>One part of several: session progress then persists no resume point, completion or history (docs/api-contract.md, 10.1).</summary>
    [JsonIgnore]
    public bool IsMultiPart => PresentationPartTotal > 1;
}

/// <summary>One video stream as the scanner probed it. Integers are 0 when unknown.</summary>
public sealed record WatchVideoTrack
{
    public string? Title { get; init; }

    public string? Codec { get; init; }

    public string? Profile { get; init; }

    /// <summary>H.264 <c>41</c> = 4.1; HEVC general_level_idc, <c>153</c> = 5.1.</summary>
    public int Level { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string? AspectRatio { get; init; }

    public bool Interlaced { get; init; }

    /// <summary>As the probe reports it, often a fraction such as <c>24000/1001</c>.</summary>
    public string? FrameRate { get; init; }

    public int Bitrate { get; init; }

    public int BitDepth { get; init; }

    public string? PixelFormat { get; init; }

    public int ReferenceFrames { get; init; }

    public string? VideoRange { get; init; }

    public string? VideoRangeType { get; init; }

    public string? ColorRange { get; init; }

    public string? ColorPrimaries { get; init; }

    public string? ColorSpace { get; init; }

    public string? ColorTransfer { get; init; }

    public bool Hdr10Plus { get; init; }

    public string? DolbyVision { get; init; }

    public int DvProfile { get; init; }

    public int DvLevel { get; init; }

    public int DvBlCompatId { get; init; }

    public bool DvConfigPresent { get; init; }

    public bool DvBlCompatIdPresent { get; init; }

    public bool DvBlPresent { get; init; }

    public bool DvRpuPresent { get; init; }

    public bool DvElPresent { get; init; }

    /// <summary><c>none</c>, <c>mel</c>, <c>fel</c> or <c>unknown</c>.</summary>
    public string? DvEnhancementLayer { get; init; }
}

/// <summary>One audio stream of a file.</summary>
public sealed record WatchAudioTrack
{
    public string? Title { get; init; }

    public string? EmbeddedTitle { get; init; }

    /// <summary>BCP 47 (ISO 639-1 where one exists), <c>und</c> when undetermined; older scans may carry ISO 639-2.</summary>
    public string? Language { get; init; }

    public string? Codec { get; init; }

    public string? Profile { get; init; }

    /// <summary>For example <c>5.1(side)</c>.</summary>
    public string? Layout { get; init; }

    public int Channels { get; init; }

    public int Bitrate { get; init; }

    public int SampleRate { get; init; }

    public int BitDepth { get; init; }

    public bool Default { get; init; }
}

/// <summary>One subtitle track of a file, embedded or external.</summary>
public sealed record WatchSubtitleTrack
{
    /// <summary>The FFmpeg stream index of an embedded track (0 when the server leaves it out); never the start request's ordinal.</summary>
    public int Index { get; init; }

    public string? Language { get; init; }

    public string? Codec { get; init; }

    public string? Title { get; init; }

    public string? EmbeddedTitle { get; init; }

    public string? Resolution { get; init; }

    public bool Forced { get; init; }

    public bool Default { get; init; }

    public bool HearingImpaired { get; init; }

    public bool External { get; init; }

    public string? FileName { get; init; }
}

/// <summary>A chapter of a file, in source seconds. mpv reads none through remux or transcode routes, so use this list.</summary>
public sealed record WatchChapter
{
    public int Index { get; init; }

    public string Title { get; init => field = value ?? ""; } = "";

    public double StartSeconds { get; init; }

    public double EndSeconds { get; init; }

    /// <summary>Where the chapter came from, for example <c>embedded</c>.</summary>
    public string Source { get; init => field = value ?? ""; } = "";

    public string? ThumbnailUrl { get; init; }

    public string? ThumbnailThumbhash { get; init; }
}

/// <summary>A skippable span in source seconds.</summary>
public sealed record WatchMarker(double StartSeconds, double EndSeconds);

/// <summary>One marker occurrence of a file (<see cref="WatchMarkerKinds"/>), in source seconds.</summary>
public sealed record WatchMarkerSegment
{
    public string Kind { get; init => field = value ?? ""; } = "";

    public double StartSeconds { get; init; }

    public double EndSeconds { get; init; }

    public string? ThumbnailUrl { get; init; }

    public string? ThumbnailThumbhash { get; init; }

    public double? ThumbnailCaptureSeconds { get; init; }
}

/// <summary>A logical watch choice (an edition or presentation) made of ordered parts.</summary>
public sealed record WatchPlaybackVariant
{
    public string VariantId { get; init => field = value ?? ""; } = "";

    public string? EditionKey { get; init; }

    public string? EditionRaw { get; init; }

    public string? PresentationKind { get; init; }

    public string? PresentationGroupKey { get; init; }

    public int PartCount { get; init; }

    /// <summary>0 when unknown.</summary>
    public long TotalDurationSeconds { get; init; }

    public string? DefaultFileId { get; init; }

    public IReadOnlyList<WatchPlaybackVariantPart> Parts { get; init => field = value ?? []; } = [];
}

/// <summary>The interchangeable files of one part.</summary>
public sealed record WatchPlaybackVariantPart
{
    /// <summary>Counted from 0.</summary>
    public int PartIndex { get; init; }

    public string? DefaultFileId { get; init; }

    /// <summary>0 when unknown.</summary>
    public long TotalDurationSeconds { get; init; }

    public IReadOnlyList<WatchFileVersion> Versions { get; init => field = value ?? []; } = [];
}

/// <summary>A subtitle available for the item.</summary>
public sealed record WatchSubtitle
{
    /// <summary><c>embedded</c> or <c>external</c>.</summary>
    public string Source { get; init => field = value ?? ""; } = "";

    public string Language { get; init => field = value ?? ""; } = "";

    public string? Codec { get; init; }

    public bool Forced { get; init; }

    public bool HearingImpaired { get; init; }

    public string? Title { get; init; }
}

/// <summary>A subtitle track identified independently of its index, as the profile last chose it.</summary>
public sealed record WatchSubtitleSignature
{
    public string? Source { get; init; }

    public string? Language { get; init; }

    public string? Codec { get; init; }

    public string? Label { get; init; }

    public bool Forced { get; init; }

    public bool HearingImpaired { get; init; }
}
