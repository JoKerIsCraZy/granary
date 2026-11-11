namespace Granary.Core.Downloads;

/// <summary>
/// One download of the active profile as the pages show it: what it is, where it stands, and how far it got.
/// <see cref="WaitsForUnmetered"/>: the connection is metered and Settings → Downloads does not allow downloads there.
/// <see cref="PosterPath"/> is a film's poster or an episode's still, <see cref="SeriesPosterPath"/> an episode's series poster;
/// each null until fetched.
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
    string? SeriesPosterPath = null)
{
    /// <summary>How much of the file is on this PC, 0 to 1; 1 once it is downloaded.</summary>
    public double Progress => State == DownloadState.Completed ? 1 : ExpectedBytes > 0 ? Math.Clamp((double)BytesDone / ExpectedBytes, 0, 1) : 0;

    /// <summary>It is downloaded and its files are there.</summary>
    public bool IsPlayable => State == DownloadState.Completed && !FilesMissing;
}
