using Granary.Core.Api.Downloads;

namespace Granary.Core.Downloads;

/// <summary>Where a download is on this PC. The viewer's pause is <see cref="DownloadRecord.IsPausedByViewer"/>, on top of it.</summary>
public enum DownloadState
{
    /// <summary>The server prepares its file (a remux or a smaller copy).</summary>
    Preparing,

    /// <summary>Waiting for its turn, or for the connection.</summary>
    Queued,

    Downloading,

    /// <summary>The whole file is on this PC.</summary>
    Completed,

    /// <summary>Stopped; <see cref="DownloadRecord.Failure"/> says why.</summary>
    Failed,
}

public enum DownloadFailure
{
    None,

    /// <summary>The drive was full: the bytes so far stay, and Resume carries on from them.</summary>
    NotEnoughSpace,

    /// <summary>The server refused or failed it.</summary>
    Server,

    /// <summary>The file arrived with another length than the server said, twice in a row.</summary>
    Integrity,
}

/// <summary>
/// One download as this PC keeps it, in <see cref="DownloadPaths.Record"/> in its folder. <see cref="MediaItemId"/> is the film's
/// or the episode's own id; <see cref="SeriesId"/> is set for an episode.
/// </summary>
public sealed record DownloadRecord
{
    public required string DownloadId { get; init; }

    public required DownloadScope Scope { get; init; }

    public required string MediaItemId { get; init; }

    public string? SeriesId { get; init; }

    /// <summary>The film's or the episode's own title.</summary>
    public required string Title { get; init; }

    public string? SeriesTitle { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    /// <summary>The version downloaded.</summary>
    public string? MediaFileId { get; init; }

    /// <summary>The preset asked for.</summary>
    public required string Quality { get; init; }

    /// <summary>The server entry's revision these bytes belong to.</summary>
    public long Revision { get; init; }

    /// <summary>The finished file's length, over 4 GB for a 4K original.</summary>
    public long ExpectedBytes { get; init; }

    public DownloadState State { get; init; }

    public DownloadFailure Failure { get; init; }

    /// <summary>The viewer paused it; it stays paused, also across starts, until they resume it.</summary>
    public bool IsPausedByViewer { get; init; }

    /// <summary>The server no longer has it for this PC: a new download of it cannot be made from this entry.</summary>
    public bool IsNoLongerAvailable { get; init; }

    /// <summary>The finished media file's name in the folder (<see cref="DownloadPaths.MediaName"/>); null until it is whole.</summary>
    public string? MediaFile { get; init; }

    /// <summary>The validator the bytes in <see cref="DownloadPaths.MediaPart"/> came with, sent back as <c>If-Range</c>.</summary>
    public string? Validator { get; init; }

    /// <summary>How far the server's preparation is, 0 to 1, while it says.</summary>
    public double? PreparationProgress { get; init; }

    /// <summary>A status event not yet taken by the server, sent again as it was.</summary>
    public DownloadStatusEvent? PendingReport { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the manifest was last read from the server.</summary>
    public DateTimeOffset? ManifestReadAt { get; init; }

    /// <summary>Each kept subtitle's revision by its reference (<c>external:0</c>), to tell which changed on the server.</summary>
    public IReadOnlyDictionary<string, string> SubtitleRevisions
    {
        get;
        init => field = value ?? new Dictionary<string, string>();
    } = new Dictionary<string, string>();

    /// <summary>The artwork kept in the folder, its file name by kind (<c>poster</c> → <c>poster.jpg</c>).</summary>
    public IReadOnlyDictionary<string, string> ArtworkFiles
    {
        get;
        init => field = value ?? new Dictionary<string, string>();
    } = new Dictionary<string, string>();
}

/// <summary>
/// A download with its folder. <see cref="FilesMissing"/>: its files cannot be used, so the row stays with what the index says
/// of it: the folder is not there (a drive that is not plugged in) until it is back, or it is <see cref="IsDamaged"/>.
/// </summary>
public sealed record StoredDownload(DownloadRecord Record, string Folder, bool FilesMissing)
{
    /// <summary>The folder is there, but its record cannot be read: it can only be deleted.</summary>
    public bool IsDamaged { get; init; }

    /// <summary>The media file while it arrives.</summary>
    public string PartPath => Path.Combine(Folder, DownloadPaths.MediaPart);

    /// <summary>The finished media file; null until it is whole, and for a name <see cref="DownloadPaths.MediaName"/> does not make.</summary>
    public string? MediaPath => DownloadPaths.IsMediaName(Record.MediaFile) ? Path.Combine(Folder, Record.MediaFile!) : null;
}

/// <summary>A server entry to delete once its profile is active and the server answers: its files are gone already.</summary>
public sealed record PendingDelete(DownloadScope Scope, string DownloadId);
