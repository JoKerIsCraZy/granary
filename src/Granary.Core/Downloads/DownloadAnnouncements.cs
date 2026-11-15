using Granary.Core.Texts;

namespace Granary.Core.Downloads;

/// <summary>
/// What is said when downloads finished or failed (docs/architecture.md, "Downloads in the shell"): <see cref="Text"/>, in the
/// language on screen, in the Apple app's words where it has them (<c>downloads.notification.*</c>, <c>downloads.failure.*</c>); <see cref="IsFailure"/>, something did not download; <see cref="PlayDownloadId"/>, the
/// download Play starts, none when nothing finished.
/// </summary>
public sealed record DownloadAnnouncement(string Text, bool IsFailure, string? PlayDownloadId);

/// <summary>
/// Which of a profile's downloads finished or failed since the last look, and how that is said. A download is announced when it
/// turns Completed or Failed between two looks; one that was so at the first look, or that shows up so (its folder came back),
/// is old news, as is one whose files were missing at the look before (a folder that was away as Granary started came back). A
/// film is announced at once. Episodes go by their series: one that ends while another of its series is still to come
/// (<see cref="DownloadTally.IsPending"/>) waits for them, so a season or a series that finishes together is one message ("8
/// episodes of Example Series are ready to watch offline"), failures counted in it; an episode paused by the viewer or
/// deleted does not hold the others back. Another scope starts afresh, its downloads old news and nothing of the last one's
/// said. Used from one thread at a time (the UI thread's <c>DownloadActivity</c>).
/// </summary>
public sealed class DownloadAnnouncements
{
    /// <summary>The Apple app's reasons (<c>downloads.failure.storageFull</c>, said of this PC; <c>sizeMismatch</c>; <c>notFound</c>).</summary>
    public static string NotEnoughStorage => AppText.Get("granary.downloads.failure.storageFull");
    public static string ArrivedIncomplete => AppText.Get("granary.downloads.failure.sizeMismatch");
    public static string NoLongerOnServer => AppText.Get("granary.downloads.failure.notFound");

    // The episodes of each series (or a film alone) that ended while others of it were still to come, in the order they ended.
    private readonly List<Batch> _batches = [];
    private DownloadScope? _scope;
    private Dictionary<string, DownloadItem>? _seen;

    /// <summary>
    /// What to say now of <paramref name="scope"/>'s downloads, as they are now (<paramref name="items"/>), in the order they come;
    /// nothing at the first look and at another scope's.
    /// </summary>
    public IReadOnlyList<DownloadAnnouncement> Look(DownloadScope? scope, IReadOnlyCollection<DownloadItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var seen = _seen;
        _seen = ById(items);
        if (seen is null || scope != _scope)
        {
            _scope = scope;
            _batches.Clear();
            return [];
        }
        foreach (var item in items)
        {
            if (seen.TryGetValue(item.DownloadId, out var before) && Ended(before, item) is { } failed)
            {
                BatchOf(GroupOf(item)).Add(item, failed);
            }
        }
        var announced = new List<DownloadAnnouncement>();
        foreach (var batch in _batches.ToList())
        {
            // One the viewer deleted while it waited is neither counted nor played; a batch left with none says nothing.
            if (!batch.KeepOnly(_seen))
            {
                _batches.Remove(batch);
            }
            else if (!items.Any(i => DownloadTally.IsPending(i) && GroupOf(i) == batch.Group))
            {
                _batches.Remove(batch);
                if (batch.Announcement(_seen) is { } announcement)
                {
                    announced.Add(announcement);
                }
            }
        }
        return announced;
    }

    // Null while it goes on; false once it finished, true once it failed, since the last look. Not one whose files were missing:
    // a download whose folder was away as Granary started is a queued row of what the index knew, and the state its folder's
    // record brings back once the folder is there again is old news.
    private static bool? Ended(DownloadItem before, DownloadItem now) => before.FilesMissing ? null : now.State switch
    {
        DownloadState.Completed when before.State != DownloadState.Completed => false,
        DownloadState.Failed when before.State != DownloadState.Failed => true,
        _ => null,
    };

    private Batch BatchOf(string group)
    {
        if (_batches.Find(b => b.Group == group) is { } batch)
        {
            return batch;
        }
        batch = new Batch(group);
        _batches.Add(batch);
        return batch;
    }

    // An episode goes with its series; a film, or an episode without its series, alone.
    private static string GroupOf(DownloadItem item) => item.SeriesId is { } series ? "series:" + series : "download:" + item.DownloadId;

    private static Dictionary<string, DownloadItem> ById(IReadOnlyCollection<DownloadItem> items)
    {
        var byId = new Dictionary<string, DownloadItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            byId[item.DownloadId] = item;
        }
        return byId;
    }

    // "Example Movie", "Example Series (S2E8)": the series' title for an episode, as the Apple app names it.
    private static string NameOf(DownloadItem item)
    {
        string name = item.SeriesTitle ?? item.Title;
        return item is { SeasonNumber: { } season, EpisodeNumber: { } episode }
            ? AppText.Get("granary.downloads.notice.episodeName", ("title", name), ("season", season), ("episode", episode))
            : name;
    }

    private static string? ReasonOf(DownloadItem item) => item switch
    {
        { IsNoLongerAvailable: true } => NoLongerOnServer,
        { Failure: DownloadFailure.NotEnoughSpace } => NotEnoughStorage,
        { Failure: DownloadFailure.Integrity } => ArrivedIncomplete,
        _ => null,
    };

    // Episodes by season and number, the specials (season 0) and the unnumbered last: Play starts the first.
    private static (int, int, int) EpisodeOrder(DownloadItem item) =>
        (item.SeasonNumber is > 0 ? 0 : 1, item.SeasonNumber ?? 0, item.EpisodeNumber ?? 0);

    private sealed class Batch(string group)
    {
        // Each download once, with how it ended last: one that failed and finished after a Retry counts as finished.
        private readonly List<(DownloadItem Item, bool Failed)> _ended = [];

        public string Group { get; } = group;

        public void Add(DownloadItem item, bool failed)
        {
            _ended.RemoveAll(e => e.Item.DownloadId == item.DownloadId);
            _ended.Add((item, failed));
        }

        /// <summary>Drops the downloads that are gone; false when none is left.</summary>
        public bool KeepOnly(IReadOnlyDictionary<string, DownloadItem> present)
        {
            _ended.RemoveAll(e => !present.ContainsKey(e.Item.DownloadId));
            return _ended.Count > 0;
        }

        /// <summary>
        /// What to say of the batch as the downloads are <paramref name="now"/>: one that finished while the others came is said
        /// ready, and played, only while it can still play (not once its drive went away or its file was deleted by hand), and
        /// one that failed only while it still stands failed (not once Retry queued it again). Each is said as it is now: its
        /// title, numbers and reason as the downloads have them then. Null when none is left to say.
        /// </summary>
        public DownloadAnnouncement? Announcement(IReadOnlyDictionary<string, DownloadItem> now)
        {
            var current = _ended.Select(e => (Now: now.GetValueOrDefault(e.Item.DownloadId), e.Failed)).Where(e => e.Now is not null).ToList();
            var finished = current.Where(e => !e.Failed && e.Now!.IsPlayable).Select(e => e.Now!).OrderBy(EpisodeOrder).ToList();
            var failed = current.Where(e => e.Failed && e.Now!.State == DownloadState.Failed).Select(e => e.Now!).ToList();
            if (finished.Count == 0 && failed.Count == 0)
            {
                return null;
            }
            string text = (finished.Count, failed.Count) switch
            {
                (> 0, 0) => Ready(finished),
                (0, _) => Failed(failed),
                _ => AppText.Get("granary.downloads.notice.alsoFailed", ("ready", Ready(finished)), ("count", failed.Count),
                    ("count_text", AppText.Number(failed.Count))),
            };
            return new DownloadAnnouncement(text, failed.Count > 0, finished.FirstOrDefault()?.DownloadId);
        }

        // The Apple app's message for one (downloads.notification.completed); Granary's for several, after it.
        private static string Ready(List<DownloadItem> finished) => finished.Count == 1
            ? AppText.Get("granary.downloads.notice.ready", ("title", NameOf(finished[0])))
            : Several(finished, "granary.downloads.notice.episodesReady", "granary.downloads.notice.episodesReadyUntitled");

        // The Apple app's message for one with its reason (downloads.notification.failed); Granary's without one, and for several.
        private static string Failed(List<DownloadItem> failed)
        {
            if (failed.Count > 1)
            {
                return Several(failed, "granary.downloads.notice.episodesFailed", "granary.downloads.notice.episodesFailedUntitled");
            }
            var item = failed[0];
            return ReasonOf(item) is { } reason
                ? AppText.Get("granary.downloads.notice.failed", ("title", NameOf(item)), ("reason", reason))
                : AppText.Get("granary.downloads.notice.failedWithoutReason", ("title", NameOf(item)));
        }

        // Several are always episodes of one series: by its title, or without one the downloads do not know.
        private static string Several(List<DownloadItem> items, string key, string untitledKey)
        {
            int count = items.Count;
            return items[0].SeriesTitle is { } series
                ? AppText.Get(key, ("count", count), ("count_text", AppText.Number(count)), ("series", series))
                : AppText.Get(untitledKey, ("count", count), ("count_text", AppText.Number(count)));
        }
    }
}
