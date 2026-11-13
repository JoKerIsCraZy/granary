using System.Globalization;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// The Downloads page's cards (docs/ui-design.md, "Downloads"), Granary's own cards with what a download has to say: a film's
/// or a series' poster, an episode's still, all from the files on this PC. Under the title the size, then the status in the
/// apps' words ("Downloading · 42%", "Paused", "Missing file"), and a bar on the artwork while the file comes. Once it is
/// whole, how much of it was watched, as a Continue Watching card says it: the bar and "18 min left", or the watched mark
/// and "Watched". Every card has those three lines, so a grid, which sizes its cards by its first, keeps its rows even.
/// </summary>
public static class DownloadCards
{
    /// <summary>The <see cref="MediaCard.Type"/> of a film's or an episode's download; its <see cref="MediaCard.Id"/> is the download's.</summary>
    public const string DownloadType = "download";

    /// <summary>The <see cref="MediaCard.Type"/> of a series' card, which stands for its downloads; its id is the series'.</summary>
    public const string SeriesType = "download_series";

    // A line kept free while a size is not known: the card keeps its three lines.
    private const string EmptyLine = "\u00A0";

    /// <summary>"Watched": a whole download the profile has watched, beside the watched mark.</summary>
    public const string Watched = "Watched";

    /// <summary>
    /// A film's download as a poster, or an episode's as a still ("S01E05 · Chapter Five", as a season's page names it). Play
    /// on the card plays it from this PC once it is whole. While it comes, its status has the speed and the time left once
    /// <paramref name="bytesPerSecond"/> is known. Once it is whole, its watch progress from the best position this PC knows:
    /// its own play's, or the server's when that is newer (<see cref="DownloadRecord.ResumeMs"/>).
    /// </summary>
    public static MediaCard Download(DownloadItem item, double? bytesPerSecond = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool episode = item.SeriesId is not null;
        bool watched = item.State == DownloadState.Completed && !item.FilesMissing && item.IsWatched;
        string status = Status(item, bytesPerSecond);
        return new MediaCard(item.DownloadId, DownloadType, episode ? EpisodeTitle(item) : item.Title)
        {
            Shape = episode ? MediaCardShape.Landscape : MediaCardShape.Poster,
            Subtitle = SizeLine(item.ExpectedBytes),
            Caption = status,
            IsCaptionAttention = NeedsAttention(item),
            // Without the speed, which changes every second.
            AccessibleStatus = Status(item, bytesPerSecond: null),
            LocalImagePath = item.PosterPath,
            Progress = Bar(item),
            IsWatched = watched,
            ShowsWatchedMark = watched,
            PlayContentId = item.IsPlayable ? item.MediaItemId : null,
        };
    }

    /// <summary>
    /// A series' downloads as its poster: the size they take and how many there are, "3 episodes", or "2 of 3 episodes" while
    /// some are not whole yet (the Android app's words), with a bar for how much of them is here. It opens their own page.
    /// </summary>
    public static MediaCard Series(string seriesId, IReadOnlyList<DownloadItem> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        string title = episodes.Count > 0 ? SeriesTitleOf(episodes[0]) : "";
        string count = EpisodeCount(episodes);
        var usable = episodes.Where(e => !e.FilesMissing).ToList();
        long expected = usable.Sum(e => e.ExpectedBytes);
        bool whole = usable.All(e => e.State == DownloadState.Completed);
        // Every episode here watched, as the Apple app's series says "all watched".
        bool watched = episodes.Count > 0 && episodes.All(e => e.IsWatched);
        return new MediaCard(seriesId, SeriesType, title)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = SizeLine(expected),
            Caption = count,
            AccessibleStatus = count,
            IsWatched = watched,
            ShowsWatchedMark = watched,
            LocalImagePath = episodes.Select(e => e.SeriesPosterPath).FirstOrDefault(p => p is not null),
            Progress = whole || expected <= 0 ? null : Math.Clamp((double)usable.Sum(e => e.BytesDone) / expected, 0, 1),
        };
    }

    /// <summary>"3 episodes", "1 episode"; "2 of 3 episodes" while some are not whole on this PC.</summary>
    public static string EpisodeCount(IReadOnlyCollection<DownloadItem> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        int total = episodes.Count;
        int whole = episodes.Count(e => e.IsPlayable);
        string noun = total == 1 ? "episode" : "episodes";
        string all = total.ToString(CultureInfo.InvariantCulture);
        return whole == total ? $"{all} {noun}" : $"{whole.ToString(CultureInfo.InvariantCulture)} of {all} {noun}";
    }

    /// <summary>The series an episode belongs to, by the title it was downloaded with; its own title when that is missing.</summary>
    public static string SeriesTitleOf(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return string.IsNullOrWhiteSpace(item.SeriesTitle) ? item.Title : item.SeriesTitle.Trim();
    }

    /// <summary>"8.0 GB", as the page has always said a download's size.</summary>
    public static string SizeText(long bytes) => PrePlayText.FileSize(bytes);

    // Where it stands: until it is whole, in the page's words, with the speed while it comes; whole, how much of it was
    // watched, unless its files are gone or the server no longer has it, which the page says first.
    private static string Status(DownloadItem item, double? bytesPerSecond)
    {
        if (item.State != DownloadState.Completed || item.FilesMissing || item.IsNoLongerAvailable)
        {
            return DownloadText.Status(item, bytesPerSecond);
        }
        if (item.IsWatched)
        {
            return Watched;
        }
        return item.WatchedShare is not null
            ? MediaCardFactory.TimeLeft(item.ResumeMs / 1000.0, item.DurationMs / 1000.0) ?? DownloadText.Downloaded
            : DownloadText.Downloaded;
    }

    // A download the viewer has to look at: it failed, its files are gone, or the server no longer has it. Amber, as a
    // watchlist title TMDB lost.
    private static bool NeedsAttention(DownloadItem item) =>
        item.FilesMissing || item.IsNoLongerAvailable || item.State == DownloadState.Failed;

    // How much of the file is here, while it comes or waits with bytes on this PC; once it is whole, how much was watched.
    private static double? Bar(DownloadItem item)
    {
        if (item.FilesMissing)
        {
            return null;
        }
        if (item.State == DownloadState.Completed)
        {
            return item.WatchedShare;
        }
        if (item.IsNoLongerAvailable || item.State == DownloadState.Preparing)
        {
            return null;
        }
        return item.Progress > 0 ? item.Progress : null;
    }

    private static string SizeLine(long bytes) => SizeText(bytes) is { Length: > 0 } size ? size : EmptyLine;

    private static string EpisodeTitle(DownloadItem item) => item is { SeasonNumber: { } season, EpisodeNumber: { } episode }
        ? string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{episode:00} · {item.Title}")
        : item.Title;
}
