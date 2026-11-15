namespace Granary.Core.Downloads;

/// <summary>What keeps a download from starting although it could: a storage limit.</summary>
public enum DownloadHold
{
    None,

    /// <summary>Its monitored series' storage limit: the series' downloads would take more than its monitor allows.</summary>
    SeriesLimit,

    /// <summary>Granary's storage limit for downloads (Settings → Downloads): the downloads on this PC would take more.</summary>
    DownloadsLimit,

    /// <summary>
    /// Whether a limit holds it is not known yet: an episode, before monitoring's first run read its profile's monitors (as at
    /// Granary's start, before which its series' limit, or that it is monitored at all, is not known).
    /// </summary>
    PolicyUnknown,
}

/// <summary>
/// The storage limits (docs/architecture.md, "Series monitoring"): a monitored series' (<c>max_storage_bytes</c>), which the
/// client holds and the server only roughly, as Silo's Apple app holds it (<c>exceedsStorageCap</c>), and Granary's own for all
/// downloads. Either holds back an episode of a monitored series whose bytes would take what it counts past the limit; it
/// starts once there is room (a download deleted, a limit raised). Nothing is deleted for them, and other downloads are never
/// held: the download dialog does not start what would pass Granary's limit.
/// </summary>
public sealed partial class DownloadQueue
{
    /// <summary>What holds one of a scope's downloads back now; <see cref="DownloadHold.None"/> when nothing does, or it is not there.</summary>
    public DownloadHold HoldOf(DownloadScope scope, string downloadId)
    {
        var all = _store.All;
        if (all.FirstOrDefault(d => d.Record.Scope == scope && d.Record.DownloadId == downloadId) is not { } download)
        {
            return DownloadHold.None;
        }
        lock (_gate)
        {
            return HoldLocked(download, TallyLocked(all));
        }
    }

    /// <summary>The scope's downloads a limit holds back now, by id, counted in one go for a page's whole list.</summary>
    public IReadOnlyDictionary<string, DownloadHold> HoldsOf(DownloadScope scope)
    {
        var all = _store.All;
        var holds = new Dictionary<string, DownloadHold>(StringComparer.Ordinal);
        lock (_gate)
        {
            var tally = TallyLocked(all);
            foreach (var download in all.Where(d => d.Record.Scope == scope))
            {
                if (HoldLocked(download, tally) is not DownloadHold.None and var hold)
                {
                    holds[download.Record.DownloadId] = hold;
                }
            }
        }
        return holds;
    }

    /// <summary>
    /// What the downloads on this PC take, every profile's: whole files at their size, a transfer under way at the size it comes
    /// to, the rest at the bytes they have. Missing files take nothing here. It reads neither the disk nor the connection.
    /// </summary>
    public long BytesTaken()
    {
        var all = _store.All;
        lock (_gate)
        {
            return all.Sum(TakenLocked);
        }
    }

    private void OnMonitorsChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            WakeLocked();
        }
    }

    // A monitored series' episode that has to come, not under way, whose bytes would take its series past its monitor's limit or
    // the downloads past Granary's, as the tally counts them. A transfer under way is never stopped for a limit.
    private DownloadHold HoldLocked(StoredDownload download, LimitTally tally)
    {
        var record = download.Record;
        var key = Key.Of(record);
        if (_monitors is null || record.SeriesId is not { } seriesId || _running.ContainsKey(key)
            || record.State is not (DownloadState.Queued or DownloadState.Downloading))
        {
            return DownloadHold.None;
        }
        if (!_monitors.IsKnown(record.Scope))
        {
            // Not "not monitored": it waits for the run that reads its profile's monitors, or finds it cannot.
            return DownloadHold.PolicyUnknown;
        }
        if (_monitors.ForSeries(record.Scope, seriesId) is not { } monitor)
        {
            return DownloadHold.None;
        }
        long own = TakenLocked(download);
        long needs = Math.Max(record.ExpectedBytes, _bytes.GetValueOrDefault(key));
        if (monitor.MaxStorageBytes > 0 && tally.SeriesTaken(record.Scope, seriesId) - own + needs > monitor.MaxStorageBytes)
        {
            return DownloadHold.SeriesLimit;
        }
        long limit = _preferences.StorageLimitGb * DownloadPreferenceLimits.BytesPerGb;
        return limit > 0 && tally.Total - own + needs > limit ? DownloadHold.DownloadsLimit : DownloadHold.None;
    }

    // What every download takes now, in all and by series, counted once for a pass or a page's list.
    private LimitTally TallyLocked(IReadOnlyList<StoredDownload> all)
    {
        var tally = new LimitTally();
        foreach (var download in all)
        {
            tally.Add(download.Record, TakenLocked(download));
        }
        return tally;
    }

    /// <summary>
    /// What the downloads take, in all and by each scope's series. A pass adds what each transfer it starts comes to, so the
    /// episodes it weighs after it count that one whole.
    /// </summary>
    private sealed class LimitTally
    {
        private readonly Dictionary<(DownloadScope Scope, string SeriesId), long> _series = [];

        public long Total { get; private set; }

        public long SeriesTaken(DownloadScope scope, string seriesId) => _series.GetValueOrDefault((scope, seriesId));

        public void Add(DownloadRecord record, long bytes)
        {
            Total += bytes;
            if (record.SeriesId is { } seriesId)
            {
                _series[(record.Scope, seriesId)] = SeriesTaken(record.Scope, seriesId) + bytes;
            }
        }
    }

    private long TakenLocked(StoredDownload download)
    {
        if (download.FilesMissing || download.MediaMissing)
        {
            return 0;
        }
        var record = download.Record;
        var key = Key.Of(record);
        long bytes = _bytes.GetValueOrDefault(key);
        return record.State == DownloadState.Completed ? record.ExpectedBytes
            : _running.ContainsKey(key) ? Math.Max(record.ExpectedBytes, bytes)
            : bytes;
    }
}
