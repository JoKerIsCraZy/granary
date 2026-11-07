using System.Text.Json;

namespace Granary.Core.Api.Downloads;

/// <summary>
/// <c>GET /api/v2/downloads/{id}/manifest</c> (downloads-api.md, 6): everything a download needs to play without the server,
/// kept beside the media as the server sent it. The members Granary reads; a missing optional one is "not set".
/// </summary>
public sealed record OfflineManifest
{
    public required string DownloadId { get; init; }

    /// <summary>The film, or an episode's series.</summary>
    public required string ContentId { get; init; }

    /// <summary>The episode, for an episode's download.</summary>
    public string? EpisodeId { get; init; }

    /// <summary><c>movie</c> or <c>episode</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Matches the entry's revision; a newer one replaces the media and this manifest.</summary>
    public long Revision { get; init; }

    public string? MediaFileId { get; init; }

    public long FileSize { get; init; }

    /// <summary>The film's or the episode's own title.</summary>
    public string? Title { get; init; }

    public int? Year { get; init; }

    public string? Overview { get; init; }

    public string? SeriesId { get; init; }

    public string? SeriesTitle { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    /// <summary>An episode's still for an episode, as <c>artwork_urls.poster</c> is.</summary>
    public string? PosterThumbhash { get; init; }

    public string? BackdropThumbhash { get; init; }

    /// <summary>Episode manifests only.</summary>
    public string? SeriesPosterThumbhash { get; init; }

    /// <summary>
    /// <c>poster</c>, <c>backdrop</c>, <c>logo</c> and <c>series_poster</c> (episodes only), each an authenticated
    /// <c>/api/v2/downloads/{id}/artwork/{kind}</c> path, present only when the image exists.
    /// </summary>
    public IReadOnlyDictionary<string, string> ArtworkUrls { get; init => field = value ?? new Dictionary<string, string>(); } =
        new Dictionary<string, string>();

    /// <summary>The delivered file's container, which names the media file on disk.</summary>
    public string? Container { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>The audio track to start with, an index into <see cref="AudioTracks"/>.</summary>
    public int? SelectedAudioTrackIndex { get; init; }

    public IReadOnlyList<OfflineAudioTrack> AudioTracks { get; init => field = value ?? []; } = [];

    public IReadOnlyList<OfflineChapter> Chapters { get; init => field = value ?? []; } = [];

    /// <summary>Every intro, credits, recap and preview range in time order; repeated kinds stay separate.</summary>
    public IReadOnlyList<OfflineMarker> MarkerSegments { get; init => field = value ?? []; } = [];

    public IReadOnlyList<OfflineSubtitle> Subtitles { get; init => field = value ?? []; } = [];

    public OfflineIntegrity? Integrity { get; init; }

    public int ManifestVersion { get; init; }

    /// <summary>A manifest as the server sent it.</summary>
    /// <exception cref="JsonException">The bytes are not a manifest.</exception>
    public static OfflineManifest Read(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, DownloadsJsonContext.Default.OfflineManifest) ?? throw new JsonException("The manifest is empty.");
}

/// <summary>An audio track of the delivered file; <see cref="Index"/> is its position among the file's audio tracks.</summary>
public sealed record OfflineAudioTrack(int Index, string? Title, string? Language, string? Codec, string? Layout, int? Channels, bool Default);

public sealed record OfflineChapter(int Index, string? Title, double StartSeconds, double EndSeconds);

/// <summary>One marker range: <c>intro</c>, <c>credits</c>, <c>recap</c> or <c>preview</c>.</summary>
public sealed record OfflineMarker(string Kind, double StartSeconds, double EndSeconds);

/// <summary>
/// A subtitle to keep beside the media. <see cref="FetchUrl"/> is an authenticated
/// <c>/api/v2/downloads/{id}/subtitles/{ref}</c> path; <see cref="Revision"/> changes when its bytes do, so a refreshed
/// manifest tells which to fetch again.
/// </summary>
public sealed record OfflineSubtitle(string? Language, string? Title, string? Format, bool Forced, bool HearingImpaired, bool External,
    string? FetchUrl, long? FileSize, string? Revision);

/// <summary>What the finished media file should be: its length, and the server's hash of the source.</summary>
public sealed record OfflineIntegrity(long ExpectedBytes, string? MediaFileHash);
