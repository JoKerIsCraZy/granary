namespace Granary.Core.Downloads;

/// <summary>What keeps a download from starting although it could: a storage limit.</summary>
public enum DownloadHold
{
    None,

    /// <summary>Its monitored series' storage limit: the series' downloads would take more than its monitor allows.</summary>
    SeriesLimit,

    /// <summary>Granary's storage limit for downloads (Settings → Downloads): the downloads on this PC would take more.</summary>
    DownloadsLimit,
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
            return HoldLocked(download, all);
        }
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
    // the downloads past Granary's. A transfer under way is never stopped for a limit.
    private DownloadHold HoldLocked(StoredDownload download, IReadOnlyList<StoredDownload> all)
    {
        var record = download.Record;
        var key = Key.Of(record);
        if (_monitors is null || record.SeriesId is not { } seriesId || _running.ContainsKey(key)
            || record.State is not (DownloadState.Queued or DownloadState.Downloading)
            || _monitors.ForSeries(record.Scope, seriesId) is not { } monitor)
        {
            return DownloadHold.None;
        }
        long needs = Math.Max(record.ExpectedBytes, _bytes.GetValueOrDefault(key));
        if (monitor.MaxStorageBytes > 0)
        {
            long series = all.Where(d => d.Record.Scope == record.Scope && d.Record.SeriesId == seriesId && Key.Of(d.Record) != key).Sum(TakenLocked);
            if (series + needs > monitor.MaxStorageBytes)
            {
                return DownloadHold.SeriesLimit;
            }
        }
        long limit = _preferences.StorageLimitGb * DownloadPreferenceLimits.BytesPerGb;
        if (limit > 0 && all.Where(d => Key.Of(d.Record) != key).Sum(TakenLocked) + needs > limit)
        {
            return DownloadHold.DownloadsLimit;
        }
        return DownloadHold.None;
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
