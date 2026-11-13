using Granary.Core.Api.Catalog;
using Granary.Core.Api.Downloads;
using Granary.Core.ViewModels.Detail;
using Granary.Core.ViewModels.Downloads;

namespace Granary.Core.Downloads;

/// <summary>What the download dialog offers once it knows the title's files (<see cref="DownloadOffers"/>).</summary>
public abstract record DownloadOffer;

/// <summary>A film or an episode: the request with the version to choose first, the title's files and its runtime.</summary>
public sealed record TitleDownloadOffer(DownloadRequest Request, IReadOnlyList<DownloadVersionRow> Versions, int RuntimeSeconds) : DownloadOffer;

/// <summary>A season's episodes that are not on this PC yet, one download each, and what their files take on the server.</summary>
public sealed record SeasonDownloadOffer(SeasonDownloadRequest Request, string SeasonTitle, long KnownBytes) : DownloadOffer;

/// <summary>Nothing to download, and why, in the words the dialog says it with.</summary>
public sealed record NoDownloadOffer(string Reason) : DownloadOffer;

/// <summary>
/// How a title page's Download offers a film, an episode and a season (<c>ItemDetailViewModel.Downloads.cs</c>), and a card's
/// once its title has been read (<see cref="DownloadOfferReader"/>): the same request, versions, sizes and episodes either way.
/// </summary>
public static class DownloadOffers
{
    /// <summary>A title without a file, or a season none of whose episodes has one: the web's words (<c>web.detail.mediaInfo.noFiles</c>).</summary>
    public const string NoFiles = "No media files for this item.";

    /// <summary>A season whose episodes with a file are all downloaded already.</summary>
    public const string SeasonOnThisPc = "Every episode of this season is on this PC.";

    /// <summary>
    /// A film, or an episode of its series, as a download of <paramref name="fileId"/> (null lets the server choose); the
    /// dialog sets the quality. Null for any other title, and for an episode whose series is not known.
    /// </summary>
    public static DownloadRequest? TitleRequest(CatalogItemDetail item, string contentId, string? fileId)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Type switch
        {
            CatalogItemTypes.Movie => new DownloadRequest(contentId, null, fileId, DownloadQualities.Original, item.Title, null, null, null),
            CatalogItemTypes.Episode when item.SeriesId is { Length: > 0 } series => new DownloadRequest(contentId, series, fileId,
                DownloadQualities.Original, item.Title, item.SeriesTitle, item.SeasonNumber, item.EpisodeNumber),
            _ => null,
        };
    }

    /// <summary>The Version picker's rows (<paramref name="pickers"/>), each with its file's size from <paramref name="files"/>.</summary>
    public static IReadOnlyList<DownloadVersionRow> TitleVersions(IReadOnlyList<FileVersion> files, PrePlayPickers pickers)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(pickers);
        return [.. pickers.Versions.Select(v => new DownloadVersionRow(v.FileId, v.Summary, files.FirstOrDefault(f => f.FileId == v.FileId)?.FileSize ?? 0))];
    }

    /// <summary>The runtime that sizes a preset: the title's minutes, else the length of <paramref name="version"/>.</summary>
    public static int RuntimeSeconds(CatalogItemDetail item, FileVersion? version)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Runtime is { } minutes && minutes > 0 ? minutes * 60 : (int)Math.Clamp(version?.Duration ?? 0, 0, int.MaxValue);
    }

    /// <summary>
    /// A film or an episode as its page offers it before the viewer picks: every file, the one playback would take chosen.
    /// Null for a title without a file and for any other kind.
    /// </summary>
    public static TitleDownloadOffer? ForTitle(CatalogItemDetail item, string contentId)
    {
        ArgumentNullException.ThrowIfNull(item);
        var pickers = PrePlayPickers.For(item, PrePlayPicks.None);
        if (pickers.Version is not { } version || TitleRequest(item, contentId, version.FileId) is not { } request)
        {
            return null;
        }
        return new TitleDownloadOffer(request, TitleVersions(item.Versions, pickers), RuntimeSeconds(item, version));
    }

    /// <summary>
    /// The season's episodes that have a file the server can read and are not on this PC yet (<paramref name="isOnThisPc"/>),
    /// each one download, so the dialog counts and sizes what starts: each one asks for its first file, the one its size
    /// counts. Null when none is left, and for a season whose series or number is not known.
    /// </summary>
    public static SeasonDownloadOffer? ForSeason(CatalogItemDetail season, IEnumerable<Episode> episodes, Func<string, bool> isOnThisPc)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(isOnThisPc);
        if (season.SeriesId is not { Length: > 0 } seriesId || season.SeasonNumber is not { } number)
        {
            return null;
        }
        var left = episodes.Where(e => e.ContentId.Length > 0 && UsableFiles(e).Count > 0 && !isOnThisPc(e.ContentId)).ToList();
        if (left.Count == 0)
        {
            return null;
        }
        string seriesTitle = !string.IsNullOrWhiteSpace(season.SeriesTitle) ? season.SeriesTitle : season.Title;
        var request = new SeasonDownloadRequest(seriesId, seriesTitle, number,
            [.. left.Select(e => new SeasonEpisode(e.ContentId, e.EpisodeNumber, e.Title, UsableFiles(e)[0].FileId))], DownloadQualities.Original);
        return new SeasonDownloadOffer(request, season.Title, left.Sum(e => Math.Max(0, UsableFiles(e)[0].FileSize)));
    }

    /// <summary>An episode's files the server can read, as the player steps over the others (<c>NextEpisode</c>).</summary>
    public static IReadOnlyList<EpisodeFile> UsableFiles(Episode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return [.. episode.Files.Where(f => f.FileId.Length > 0 && !f.Unreadable)];
    }
}
