using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels.Downloads;

/// <summary>
/// The Downloads page's Monitored cards, as the Apple app's Monitored rows say a series (<c>AutoDownloadRow</c>) in the page's
/// own cards: the series' poster and title, what the monitor takes ("Future episodes · 10 Mbps"), and what it does next
/// ("Episode 6 on Thursday"), amber when a storage limit holds an episode back.
/// </summary>
public static class MonitorCards
{
    /// <summary>The series' card for <paramref name="monitor"/>; its id is the series'.</summary>
    public static MediaCard Card(DownloadSubscription monitor, string title, MonitoredSeriesInfo? info, MonitorStatus status, DateTimeOffset now,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        string rule = MonitorRules.Summary(monitor);
        string line = MonitorRules.StatusLine(status, now, zone);
        return new MediaCard(monitor.SeriesId, DownloadCards.MonitorType, title)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = rule,
            PlainSubtitle = true,
            Caption = line,
            IsCaptionAttention = status is MonitorStatus.StorageLimit,
            AccessibleLines = $"{rule}, {line}",
            ToolTip = $"{rule} · {line}",
            ImageUrl = info?.Poster,
            ImageThumbhash = info?.PosterThumbhash,
        };
    }

    /// <summary>
    /// A series' downloads still on their way, as its monitor's status counts them: not one the viewer paused, which is their
    /// choice and not something the series waits on.
    /// </summary>
    public static IReadOnlyList<MonitorActivity> ActivityOf(IEnumerable<DownloadItem> items, string seriesId) =>
    [
        .. items.Where(i => i.SeriesId == seriesId && !i.IsPausedByViewer && !i.FilesMissing && !i.IsNoLongerAvailable
                && i.State is DownloadState.Preparing or DownloadState.Queued or DownloadState.Downloading)
            .Select(i => new MonitorActivity(i.SeasonNumber, i.EpisodeNumber, PhaseOf(i), i.Progress)),
    ];

    /// <summary>The series' episodes this PC has or is getting; a failed one does not count, so it can still be named next.</summary>
    public static IReadOnlyList<string> KnownEpisodes(IEnumerable<DownloadItem> items, string seriesId) =>
        [.. items.Where(i => i.SeriesId == seriesId && i.State != DownloadState.Failed).Select(i => i.MediaItemId)];

    private static MonitorPhase PhaseOf(DownloadItem item) => item switch
    {
        { Hold: not DownloadHold.None } => MonitorPhase.StorageLimit,
        { State: DownloadState.Queued, WaitsForUnmetered: true } => MonitorPhase.WaitingForUnmetered,
        { State: DownloadState.Downloading } => MonitorPhase.Downloading,
        { State: DownloadState.Preparing } => MonitorPhase.Preparing,
        _ => MonitorPhase.Queued,
    };
}
