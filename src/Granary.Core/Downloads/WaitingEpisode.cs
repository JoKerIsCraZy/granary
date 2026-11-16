namespace Granary.Core.Downloads;

/// <summary>
/// An episode of a season's or a series' download that the server's limit of downloads for the account refused, kept on this PC
/// to be asked for again once the server allows more (docs/architecture.md, "Waiting episodes"): its profile, its series, its
/// numbers and title, and the file and quality the dialog chose. The store keeps them in line, the first refused first.
/// </summary>
public sealed record WaitingEpisode(DownloadScope Scope, string SeriesId, string SeriesTitle, int SeasonNumber, string EpisodeId, int EpisodeNumber,
    string Title, string? MediaFileId, string Quality)
{
    /// <summary>
    /// The store's <see cref="DownloadStore.ClearGeneration"/> when this run kept it; 0 for one read from the disk. A sign-out's
    /// removal takes only the episodes kept before its clear, as it takes downloads.
    /// </summary>
    internal long ClearsSeen { get; init; }

    /// <summary>The create it waits for, as the season's or the series' run would have sent it.</summary>
    public DownloadRequest Request => new(EpisodeId, SeriesId, MediaFileId, Quality, Title, SeriesTitle, SeasonNumber, EpisodeNumber);
}
