using System.Globalization;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// The Downloads page's cards (docs/ui-design.md, "Downloads"), Granary's own cards with what a download has to say: a film's
/// or a series' poster, an episode's still, all from the files on this PC. Under the title the size, then the status in the
/// apps' words ("Queued", "Paused", "Missing file"), both in the caption's style; while the file comes, its status over both
/// lines, each short enough for the narrowest poster, the whole line in the card's tip, and a bar on the artwork. Once it is
/// whole, how much of it was watched, as a Continue Watching card says it: the bar and "18 min left", or the watched mark
/// and "Watched". Every card has those three lines, so a grid, which sizes its cards by its first, keeps its rows even.
/// Screen readers hear the size and a status that does not change while the bar moves.
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
        bool whole = item.State == DownloadState.Completed && !item.FilesMissing && !item.IsNoLongerAvailable;
        var (upper, lower) = Lines(item, bytesPerSecond);
        return new MediaCard(item.DownloadId, DownloadType, episode ? EpisodeTitle(item) : item.Title)
        {
            Shape = episode ? MediaCardShape.Landscape : MediaCardShape.Poster,
            Subtitle = upper,
            PlainSubtitle = true,
            Caption = lower,
            IsCaptionAttention = !DownloadText.IsComing(item) && NeedsAttention(item),
            // Neither the speed nor the percentage, which change as it comes: the bar has the progress.
            AccessibleLines = string.Join(", ", new[] { SizeText(item.ExpectedBytes), SteadyStatus(item) }.Where(s => s.Length > 0)),
            ToolTip = whole ? null : DownloadText.Status(item, bytesPerSecond),
            LocalImagePath = item.PosterPath,
            Progress = Bar(item),
            IsWatched = watched,
            ShowsWatchedMark = watched,
            PlayContentId = item.IsPlayable ? item.MediaItemId : null,
        };
    }

    // The two lines under the title. While the file comes, its status over both, each short enough for the narrowest poster:
    // "Downloading · 42%" over its size until the speed is known, then "42% · 4 min left" over "12.3 MB/s". Any other time,
    // the size over the status.
    private static (string Upper, string Lower) Lines(DownloadItem item, double? bytesPerSecond)
    {
        string size = SizeLine(item.ExpectedBytes);
        if (!DownloadText.IsComing(item))
        {
            return (size, Status(item, bytesPerSecond: null));
        }
        if (!DownloadText.IsMeasured(item, bytesPerSecond))
        {
            return (DownloadText.Status(item), size);
        }
        double rate = bytesPerSecond!.Value;
        string speed = DownloadText.Speed(rate);
        return DownloadText.TimeLeft(item, rate) is { } left
            ? ($"{DownloadText.PercentText(item)} · {left}", speed)
            : (DownloadText.Status(item), speed);
    }

    // Where it stands without what changes as it comes: "Downloading", "Preparing…".
    private static string SteadyStatus(DownloadItem item)
    {
        if (DownloadText.IsComing(item))
        {
            return DownloadText.Downloading;
        }
        bool held = item.FilesMissing || item.IsNoLongerAvailable || item.IsPausedByViewer;
        return !held && item.State == DownloadState.Preparing ? DownloadText.Preparing : Status(item, bytesPerSecond: null);
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
        double here = expected > 0 ? Math.Clamp((double)usable.Sum(e => e.BytesDone) / expected, 0, 1) : 0;
        // Every episode here watched, as the Apple app's series says "all watched".
        bool watched = episodes.Count > 0 && episodes.All(e => e.IsWatched);
        return new MediaCard(seriesId, SeriesType, title)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = SizeLine(expected),
            PlainSubtitle = true,
            Caption = count,
            AccessibleLines = string.Join(", ", new[] { SizeText(expected), count }.Where(s => s.Length > 0)),
            IsWatched = watched,
            ShowsWatchedMark = watched,
            LocalImagePath = episodes.Select(e => e.SeriesPosterPath).FirstOrDefault(p => p is not null),
            // No bar before any of their bytes came, as a download's card has none.
            Progress = whole || here <= 0 ? null : here,
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
