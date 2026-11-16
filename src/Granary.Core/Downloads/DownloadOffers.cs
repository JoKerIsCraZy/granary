using System.Collections.Frozen;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Downloads;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Detail;
using Granary.Core.ViewModels.Downloads;

namespace Granary.Core.Downloads;

/// <summary>What the download dialog offers once it knows the title's files (<see cref="DownloadOffers"/>).</summary>
public abstract record DownloadOffer;

/// <summary>A film or an episode: the request with the version to choose first, the title's files and its runtime.</summary>
public sealed record TitleDownloadOffer(DownloadRequest Request, IReadOnlyList<DownloadVersionRow> Versions, int RuntimeSeconds) : DownloadOffer;

/// <summary>
/// What the dialog knows of a season's or a series' episodes beyond what their creates ask for: the ones the viewer watched,
/// which "Only unwatched episodes" leaves out, and what each one's file takes on the server. None when it is not known.
/// </summary>
public sealed record EpisodeFacts(IReadOnlySet<string> Watched, IReadOnlyDictionary<string, long> Bytes)
{
    public static EpisodeFacts None { get; } = new(FrozenSet<string>.Empty, FrozenDictionary<string, long>.Empty);

    public bool IsWatched(SeasonEpisode episode) => Watched.Contains(episode.EpisodeId);

    /// <summary>What the files of <paramref name="episodes"/> take on the server, as far as it is known.</summary>
    public long BytesOf(IEnumerable<SeasonEpisode> episodes) => episodes.Sum(e => Math.Max(0, Bytes.GetValueOrDefault(e.EpisodeId)));
}

/// <summary>A season's episodes that are not on this PC yet, one download each, and what their files take on the server.</summary>
public sealed record SeasonDownloadOffer(SeasonDownloadRequest Request, string SeasonTitle, long KnownBytes) : DownloadOffer
{
    public EpisodeFacts Facts { get; init; } = EpisodeFacts.None;

    /// <summary>One of the episodes is watched: "Only unwatched episodes" has something to leave out.</summary>
    public bool HasWatched => Request.Episodes.Any(Facts.IsWatched);

    /// <summary>The offer without the watched episodes; one in progress stays.</summary>
    public SeasonDownloadOffer Unwatched()
    {
        List<SeasonEpisode> kept = [.. Request.Episodes.Where(e => !Facts.IsWatched(e))];
        return this with { Request = Request with { Episodes = kept }, KnownBytes = Facts.BytesOf(kept) };
    }
}

/// <summary>A series' episodes that are not on this PC yet, season by season, one download each, and what their files take on the server.</summary>
public sealed record SeriesDownloadOffer(SeriesDownloadRequest Request, long KnownBytes) : DownloadOffer
{
    public EpisodeFacts Facts { get; init; } = EpisodeFacts.None;

    /// <summary>One of the episodes is watched: "Only unwatched episodes" has something to leave out.</summary>
    public bool HasWatched => Request.Seasons.Any(s => s.Episodes.Any(Facts.IsWatched));

    /// <summary>The offer without the watched episodes, and without the seasons that had only those.</summary>
    public SeriesDownloadOffer Unwatched()
    {
        List<SeriesSeason> kept = [.. Request.Seasons.Select(s => s with { Episodes = [.. s.Episodes.Where(e => !Facts.IsWatched(e))] })
            .Where(s => s.Episodes.Count > 0)];
        return this with { Request = Request with { Seasons = kept }, KnownBytes = Facts.BytesOf(kept.SelectMany(s => s.Episodes)) };
    }
}

/// <summary>Nothing to download, and why, in the words the dialog says it with.</summary>
public sealed record NoDownloadOffer(string Reason) : DownloadOffer;

/// <summary>
/// How a title page's Download offers a film, an episode, a season and a series (<c>ItemDetailViewModel.Downloads.cs</c>), and
/// a card's once its title has been read (<see cref="DownloadOfferReader"/>): the same request, versions, sizes and episodes
/// either way.
/// </summary>
public static class DownloadOffers
{
    /// <summary>A title without a file, or a season none of whose episodes has one: the web's words (<c>web.detail.mediaInfo.noFiles</c>).</summary>
    public static string NoFiles => AppText.Get("web.detail.mediaInfo.noFiles");

    /// <summary>A season whose episodes with a file are all downloaded already.</summary>
    public static string SeasonOnThisPc => AppText.Get("granary.downloads.seasonOnThisPc");

    /// <summary>A series whose episodes with a file are all downloaded already.</summary>
    public static string SeriesOnThisPc => AppText.Get("granary.downloads.seriesOnThisPc");

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
        // By number, whatever order the server lists them in: the queue takes the oldest first, so the first comes first.
        var left = episodes.Where(e => e.ContentId.Length > 0 && UsableFiles(e).Count > 0 && !isOnThisPc(e.ContentId))
            .OrderBy(e => e.EpisodeNumber).ToList();
        if (left.Count == 0)
        {
            return null;
        }
        string seriesTitle = !string.IsNullOrWhiteSpace(season.SeriesTitle) ? season.SeriesTitle : season.Title;
        var request = new SeasonDownloadRequest(seriesId, seriesTitle, number,
            [.. left.Select(e => new SeasonEpisode(e.ContentId, e.EpisodeNumber, e.Title, UsableFiles(e)[0].FileId))], DownloadQualities.Original);
        return new SeasonDownloadOffer(request, season.Title, left.Sum(FirstFileBytes)) { Facts = FactsOf(left) };
    }

    /// <summary>
    /// Every season's episodes that have a file the server can read and are not on this PC yet, as <see cref="ForSeason"/> offers
    /// one season's: the seasons by number as a series' page lists them, the specials last, so the queue, which takes the oldest
    /// first, brings the first episode first. Silo's apps offer a series' every season ("All Episodes"). Null when none is left.
    /// </summary>
    public static SeriesDownloadOffer? ForSeries(CatalogItemDetail series, string seriesId,
        IReadOnlyList<(Season Season, IReadOnlyList<Episode> Episodes)> seasons, Func<string, bool> isOnThisPc)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentException.ThrowIfNullOrEmpty(seriesId);
        ArgumentNullException.ThrowIfNull(seasons);
        ArgumentNullException.ThrowIfNull(isOnThisPc);
        var left = seasons
            .OrderBy(s => DetailFacts.IsSpecials(s.Season) ? 1 : 0).ThenBy(s => s.Season.SeasonNumber)
            .Select(s => (s.Season.SeasonNumber, Episodes: s.Episodes.Where(e => e.ContentId.Length > 0 && UsableFiles(e).Count > 0
                && !isOnThisPc(e.ContentId)).OrderBy(e => e.EpisodeNumber).ToList()))
            .Where(s => s.Episodes.Count > 0)
            .ToList();
        if (left.Count == 0)
        {
            return null;
        }
        var request = new SeriesDownloadRequest(seriesId, series.Title, [.. left.Select(s => new SeriesSeason(s.SeasonNumber,
            [.. s.Episodes.Select(e => new SeasonEpisode(e.ContentId, e.EpisodeNumber, e.Title, UsableFiles(e)[0].FileId))]))],
            DownloadQualities.Original);
        var episodes = left.SelectMany(s => s.Episodes).ToList();
        return new SeriesDownloadOffer(request, episodes.Sum(FirstFileBytes)) { Facts = FactsOf(episodes) };
    }

    /// <summary>An episode's files the server can read, as the player steps over the others (<c>NextEpisode</c>).</summary>
    public static IReadOnlyList<EpisodeFile> UsableFiles(Episode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return [.. episode.Files.Where(f => f.FileId.Length > 0 && !f.Unreadable)];
    }

    /// <summary>
    /// The viewer watched the episode to its end. One in progress is not watched, so "Only unwatched episodes" keeps it: the
    /// viewer half-way through the seventh gets the seventh to the end.
    /// </summary>
    public static bool IsWatched(Episode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return episode.UserData?.Played == true;
    }

    // Each episode's first file, the one it asks for: what the dialog's size counts.
    private static long FirstFileBytes(Episode episode) => Math.Max(0, UsableFiles(episode)[0].FileSize);

    private static EpisodeFacts FactsOf(IReadOnlyCollection<Episode> episodes) => new(
        episodes.Where(IsWatched).Select(e => e.ContentId).ToFrozenSet(StringComparer.Ordinal),
        episodes.DistinctBy(e => e.ContentId, StringComparer.Ordinal).ToFrozenDictionary(e => e.ContentId, FirstFileBytes, StringComparer.Ordinal));
}
