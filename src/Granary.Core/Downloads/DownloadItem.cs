namespace Granary.Core.Downloads;

/// <summary>
/// One download of the active profile as the pages show it: what it is, where it stands, and how far it got.
/// <see cref="WaitsForUnmetered"/>: the connection is metered and Settings → Downloads does not allow downloads there.
/// <see cref="PosterPath"/> is a film's poster or an episode's still, <see cref="SeriesPosterPath"/> an episode's series poster;
/// each null until fetched. <see cref="IsWatched"/>: the profile has watched it for sure (<see cref="DownloadRecord.IsWatched"/>).
/// <see cref="IsPlaying"/>: a player of this app has it open now (<see cref="PlayingDownloads"/>). <see cref="ResumeMs"/> and
/// <see cref="DurationMs"/>: where the profile left it and how long it is, as the record keeps them
/// (<see cref="DownloadRecord.ResumeMs"/>, <see cref="DownloadRecord.DurationMs"/>).
/// </summary>
public sealed record DownloadItem(
    string DownloadId,
    string MediaItemId,
    string Title,
    string? SeriesId,
    string? SeriesTitle,
    int? SeasonNumber,
    int? EpisodeNumber,
    DownloadState State,
    DownloadFailure Failure,
    bool IsPausedByViewer,
    bool IsNoLongerAvailable,
    bool FilesMissing,
    bool WaitsForUnmetered,
    long ExpectedBytes,
    long BytesDone,
    double? PreparationProgress,
    string? PosterPath,
    string? MediaFileId,
    string? SeriesPosterPath = null,
    bool IsWatched = false,
    bool IsPlaying = false,
    long? ResumeMs = null,
    long? DurationMs = null)
{
    /// <summary>How much of the file is on this PC, 0 to 1; 1 once it is downloaded.</summary>
    public double Progress => State == DownloadState.Completed ? 1 : ExpectedBytes > 0 ? Math.Clamp((double)BytesDone / ExpectedBytes, 0, 1) : 0;

    /// <summary>It is downloaded and its files are there.</summary>
    public bool IsPlayable => State == DownloadState.Completed && !FilesMissing;

    /// <summary>
    /// The Downloads page may suggest deleting it to free up space: it is whole on this PC and watched. Not one the server no
    /// longer has, which could not be downloaded again (Silo's Apple app leaves revoked downloads out the same way), and not
    /// one that plays now, as during its credits under a minimized player (Silo's Android app leaves the playing file out).
    /// </summary>
    public bool IsReclaimable => IsPlayable && IsWatched && !IsNoLongerAvailable && !IsPlaying;

    /// <summary>
    /// How much of it was watched, 0 to 1, when a watch is under way: a position before the end of a length that is known.
    /// Null when it was not started, when it is watched, and when its length is not known.
    /// </summary>
    public double? WatchedShare =>
        !IsWatched && ResumeMs is long position and > 0 && DurationMs is long length and > 0 && position < length
            ? (double)position / length
            : null;
}
