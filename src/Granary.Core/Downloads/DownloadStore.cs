using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// The downloads on this PC (docs/architecture.md, "Downloads"). Each lives in its own folder,
/// <see cref="DownloadPaths.Folder"/> under the downloads folder that was chosen when it started, with its record in
/// <see cref="DownloadPaths.Record"/>; <c>downloads.json</c> in the data folder lists the folders. A folder that is missing, as on
/// a drive that is not plugged in, keeps its row from the index (<see cref="StoredDownload.FilesMissing"/>) until
/// <see cref="RecheckFolders"/> finds it again; one whose record cannot be read is <see cref="StoredDownload.IsDamaged"/>.
/// </summary>
/// <remarks>
/// A change applies at once to what <see cref="In"/> and <see cref="All"/> answer, and <see cref="Changed"/> is raised on the
/// caller's thread. Files are written one at a time behind a gate, off the caller's thread, each time with the latest state, so
/// the last write is the latest. A record that cannot be written is logged, and an index that cannot be written is written
/// again with every following change; the change holds for this run. A removed download's folder is deleted only once the
/// index without it is on disk, and tried again while something holds it or its drive is away, less often each time.
/// </remarks>
public sealed partial class DownloadStore
{
    private readonly string _indexPath;
    private readonly IDownloadPreferences _preferences;
    private readonly TimeProvider _time;
    private readonly ILogger<DownloadStore> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writes = new(1, 1);

    // The damaged folders the log has named, so a recheck does not name them again.
    private readonly HashSet<string> _damagedSaid = new(StringComparer.OrdinalIgnoreCase);
    private Snapshot _state = Snapshot.Empty;
    private Task? _load;

    // The index on disk is older than the state: every write writes it until one gets through.
    private volatile bool _indexDirty;

    // The index as last written; only the writes, one at a time, touch it.
    private byte[]? _writtenIndex;

    // When each removed download's folder that could not be deleted yet is tried again.
    private readonly Dictionary<FolderDelete, FolderRetry> _folderRetries = [];

    public DownloadStore(string indexPath, IDownloadPreferences preferences, TimeProvider time, ILogger<DownloadStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);
        ArgumentNullException.ThrowIfNull(preferences);
        _indexPath = Path.GetFullPath(indexPath);
        _preferences = preferences;
        _time = time;
        _logger = logger;
    }

    /// <summary>The longest a removed download's folder that could not be deleted waits for its next try.</summary>
    public static readonly TimeSpan MaxFolderDeleteWait = TimeSpan.FromMinutes(10);

    // The first wait after a folder could not be deleted; each one after doubles it.
    private static readonly TimeSpan FirstFolderDeleteWait = TimeSpan.FromMinutes(1);

    /// <summary><c>downloads.json</c> in the data folder.</summary>
    public static string DefaultIndexPath => Path.Combine(AppPaths.DataDirectory, "downloads.json");

    /// <summary>
    /// Raised after each change, on the thread that made it (a load's on the thread that asked for it first), and once a removed
    /// download's folder went.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Every download of every scope on this PC.</summary>
    public IReadOnlyList<StoredDownload> All => Current.Downloads;

    /// <summary>
    /// The folders' deletes and index write a <see cref="RecheckFolders"/> or a <see cref="RemoveWhereAsync"/> started; the queue
    /// waits for them. It never fails: what fails in it is logged, and the folders still to delete are tried again.
    /// </summary>
    internal Task FolderWork { get; private set; } = Task.CompletedTask;

    /// <summary>For tests: runs once a delete pass has written its index, before it deletes that index's folders.</summary>
    internal Action? IndexWritten { get; set; }

    /// <summary>For tests: runs once a delete pass has deleted its folders.</summary>
    internal Action? FoldersDeleted { get; set; }

    /// <summary>For tests: runs once a new download's folder is made, before its record is written there.</summary>
    internal Action<string>? FolderMade { get; set; }

    /// <summary>For tests: runs on the load's thread before the index is read, as a drive that does not answer holds it.</summary>
    internal Action? Loading { get; set; }

    /// <summary>For tests: runs on the write's thread before a changed index is written.</summary>
    internal Action? WritingIndex { get; set; }

    /// <summary>For tests: runs with a download's folder before <see cref="BytesOnDisk"/> reads its files.</summary>
    internal Action<string>? MeasuringFolder { get; set; }

    /// <summary>The most folders a load's look for download folders lists; it says so in the log when it stops there.</summary>
    internal int ScanLimit { get; init; } = 2000;

    /// <summary>What the last load's look for download folders did.</summary>
    internal ScanCount LastScan { get; private set; } = new(0, 0, false);

    private Snapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Reads the index once; every later call answers the same load, and one that failed is tried again. Writes wait for it.</summary>
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_load is null || _load.IsFaulted || _load.IsCanceled)
            {
                _load = LoadOnceAsync();
            }
            return _load.WaitAsync(cancellationToken);
        }
    }

    /// <summary>One scope's downloads, oldest first.</summary>
    public IReadOnlyList<StoredDownload> In(DownloadScope scope) => [.. Current.Downloads.Where(d => d.Record.Scope == scope)];

    public StoredDownload? Find(DownloadScope scope, string downloadId) =>
        Current.Downloads.FirstOrDefault(d => d.Record.Scope == scope && d.Record.DownloadId == downloadId);

    /// <summary>The scope's download of a film or an episode, by its own id.</summary>
    public StoredDownload? FindByItem(DownloadScope scope, string mediaItemId) =>
        Current.Downloads.FirstOrDefault(d => d.Record.Scope == scope && d.Record.MediaItemId == mediaItemId);

    /// <summary>The server deletes still to be sent for <paramref name="scope"/>.</summary>
    public IReadOnlyList<PendingDelete> PendingDeletes(DownloadScope scope) => [.. Current.PendingDeletes.Where(d => d.Scope == scope)];

    /// <summary>
    /// When a recheck next tries a removed download's folder that is still to be deleted, of any scope; null when none is. A
    /// folder not tried yet is due now.
    /// </summary>
    internal DateTimeOffset? NextFolderDeleteAt
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                return _state.FolderDeletes.Count == 0 ? null : _state.FolderDeletes.Min(d => _folderRetries.TryGetValue(d, out var retry) ? retry.At : now);
            }
        }
    }

    /// <summary>
    /// The folder of a removed download that is still to be deleted (something held it, or its drive is not there); null when
    /// none is. A server delete waits while it is there: the files go first.
    /// </summary>
    public string? FolderBeingDeleted(DownloadScope scope, string downloadId) =>
        Current.FolderDeletes.FirstOrDefault(d => d.Scope == scope && d.DownloadId == downloadId)?.Folder;

    /// <summary>
    /// Keeps a new download in its folder under the downloads folder chosen now, which starts empty. Throws, and keeps nothing,
    /// when the folder cannot be made or the record not written.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The scope already has a download with this id, or its downloads were cleared (<see cref="MarkCleared"/>) while it was added.
    /// </exception>
    /// <exception cref="IOException">The folder or the record could not be written, or the index not before the folder was made.</exception>
    public async Task<StoredDownload> AddAsync(DownloadRecord record, CancellationToken cancellationToken) =>
        await AddUnlessClearedAsync(record, ClearGeneration, cancellationToken)
        ?? throw new InvalidOperationException("The downloads of this account were cleared while it was added.");

    /// <summary>
    /// As <see cref="AddAsync"/> for a download whose create began at <paramref name="clearGeneration"/> (the
    /// <see cref="ClearGeneration"/> then): null, and nothing kept, when its scope's downloads were cleared since, as by a sign-out
    /// while the server made it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scope already has a download with this id.</exception>
    /// <exception cref="IOException">The folder or the record could not be written, or the index not before the folder was made.</exception>
    public async Task<StoredDownload?> AddUnlessClearedAsync(DownloadRecord record, long clearGeneration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await LoadAsync(cancellationToken);
        string folder = DownloadPaths.Folder(_preferences.Folder, record.Scope, record.DownloadId);
        var stored = new StoredDownload(record, folder, FilesMissing: false);
        StoredDownload? kept;
        await _writes.WaitAsync(cancellationToken);
        try
        {
            if (Find(record.Scope, record.DownloadId) is not null)
            {
                throw new InvalidOperationException("This download is kept already.");
            }
            kept = await Task.Run(() => AddToDisk(stored, clearGeneration), CancellationToken.None);
        }
        finally
        {
            _writes.Release();
        }
        if (kept is null)
        {
            _logger.LogInformation("Download {DownloadId} is not kept: its account's downloads were cleared meanwhile", record.DownloadId);
            return null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return kept;
    }

    /// <summary>
    /// Changes a download's record. The change holds at once; it is written to its folder unless its files are missing (the
    /// folder's own record wins when it is back), and a folder found gone turns the row into missing files.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The scope has no download with this id (any more).</exception>
    /// <exception cref="ArgumentException">The change gave the record another id or scope.</exception>
    public async Task<StoredDownload> UpdateAsync(DownloadScope scope, string downloadId, Func<DownloadRecord, DownloadRecord> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await LoadAsync(cancellationToken);
        StoredDownload? before = null;
        StoredDownload? updated = null;
        Change(state =>
        {
            before = state.Downloads.FirstOrDefault(d => d.Record.Scope == scope && d.Record.DownloadId == downloadId)
                ?? throw new KeyNotFoundException("This download is not kept here.");
            var record = change(before.Record);
            if (record.Scope != scope || record.DownloadId != downloadId)
            {
                throw new ArgumentException("A download keeps its id and scope.", nameof(change));
            }
            // A media file that was missing is another matter once the download changed state or names another file.
            updated = before with
            {
                Record = record,
                MediaMissing = before.MediaMissing && record.State == before.Record.State && record.MediaFile == before.Record.MediaFile,
            };
            return state with { Downloads = Replace(state.Downloads, before, updated) };
        });
        bool indexChanged = DownloadIndexEntry.Of(before!) != DownloadIndexEntry.Of(updated!);
        bool missing;
        // A handler that throws does not keep the change from being written; its exception comes after.
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            missing = await PersistAsync(() => WriteRecordOf(scope, downloadId, indexChanged));
        }
        if (missing)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return Find(scope, downloadId) ?? updated!;
    }

    /// <summary>
    /// Takes a download off this PC: its row goes at once, its folder once the index without it is written. With
    /// <paramref name="queueServerDelete"/>, the server's entry is kept as a <see cref="PendingDelete"/> until it is sent. One that
    /// is not kept here is no change.
    /// </summary>
    public async Task RemoveAsync(DownloadScope scope, string downloadId, bool queueServerDelete, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        StoredDownload? removed = null;
        Change(state =>
        {
            removed = state.Downloads.FirstOrDefault(d => d.Record.Scope == scope && d.Record.DownloadId == downloadId);
            if (removed is null)
            {
                return state;
            }
            var delete = new PendingDelete(scope, downloadId) { ClearsSeen = _clearGeneration };
            bool queued = state.PendingDeletes.Any(d => d.Scope == scope && d.DownloadId == downloadId);
            return new Snapshot(
                [.. state.Downloads.Where(d => d != removed)],
                queueServerDelete && !queued ? [.. state.PendingDeletes, delete] : state.PendingDeletes,
                [.. state.FolderDeletes, new FolderDelete(scope, downloadId, removed.Folder)]);
        });
        if (removed is null)
        {
            return;
        }
        await RaiseThenPersistAsync();
    }

    /// <summary>
    /// Takes every download and pending delete of the scopes <paramref name="match"/> picks off this PC (a sign-out whose clear
    /// is <paramref name="clearGeneration"/>, from <see cref="MarkCleared"/>). Only downloads and deletes queued before that clear
    /// go, those read from the disk included: a removal that waits for a slow drive to be read takes no download, and no
    /// delete still to send, of the same account signed in again since. The rows go at once, and the call ends once the index without them is written, or could not be
    /// (it is written again with the next change). Their folders go after that, beside the caller (<see cref="FolderWork"/>),
    /// and are tried again as any removed download's: a drive or a share that does not answer holds no sign-out up.
    /// </summary>
    public async Task RemoveWhereAsync(Func<DownloadScope, bool> match, long clearGeneration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(match);
        await LoadAsync(cancellationToken);
        bool changed = false;
        Change(state =>
        {
            var removed = state.Downloads.Where(d => match(d.Record.Scope) && d.ClearsSeen < clearGeneration).ToList();
            var deletes = state.PendingDeletes.Where(d => !match(d.Scope) || d.ClearsSeen >= clearGeneration).ToList();
            changed = removed.Count > 0 || deletes.Count != state.PendingDeletes.Count;
            return changed
                ? new Snapshot([.. state.Downloads.Except(removed)], deletes,
                    [.. state.FolderDeletes, .. removed.Select(d => new FolderDelete(d.Record.Scope, d.Record.DownloadId, d.Folder))])
                : state;
        });
        if (!changed)
        {
            return;
        }
        // The queue hears first and stops the rows' transfers. A handler that throws does not keep the change from being
        // written; its exception comes after.
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            await PersistAsync(() => WriteIndex(Current));
            DeleteFoldersBeside();
        }
    }

    /// <summary>The server took the delete, or never needs it: it is not sent again.</summary>
    public async Task ForgetPendingDeleteAsync(PendingDelete delete, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delete);
        await LoadAsync(cancellationToken);
        bool changed = false;
        Change(state =>
        {
            changed = state.PendingDeletes.Contains(delete);
            return changed ? state with { PendingDeletes = [.. state.PendingDeletes.Where(d => d != delete)] } : state;
        });
        if (!changed)
        {
            return;
        }
        await RaiseThenPersistAsync();
    }

    /// <summary>
    /// A play found the finished media file of a download gone from its folder: its row says so from now on
    /// (<see cref="StoredDownload.MediaMissing"/>), until a look at the folders finds the file again. One that is not kept here,
    /// or is marked already, is no change; nor is one whose bytes were fetched again since <paramref name="looked"/> was
    /// taken (another revision, or another file name): the file that was looked for is not the one the row names now.
    /// </summary>
    public void MarkMediaMissing(StoredDownload looked)
    {
        ArgumentNullException.ThrowIfNull(looked);
        var sought = looked.Record;
        bool changed = false;
        Change(state =>
        {
            var download = state.Downloads.FirstOrDefault(d => d.Record.Scope == sought.Scope && d.Record.DownloadId == sought.DownloadId);
            if (download is not { MediaMissing: false, FilesMissing: false, Record.State: DownloadState.Completed }
                || download.Record.Revision != sought.Revision || download.Record.MediaFile != sought.MediaFile)
            {
                return state;
            }
            changed = true;
            return state with { Downloads = Replace(state.Downloads, download, download with { MediaMissing = true }) };
        });
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Looks at every folder again: one that came back is read from its own record, one that went turns into missing files,
    /// one whose record cannot be read is damaged, and a finished download's media file is looked for
    /// (<see cref="StoredDownload.MediaMissing"/>). <see cref="Changed"/> follows when anything changed. Removed downloads'
    /// folders still there whose next try is due, and an index not yet written, are tried again in the background
    /// (<see cref="FolderWork"/>).
    /// </summary>
    /// <remarks>It reads the disk on the caller's thread, a drive that is slow to answer included: never call it on the UI thread.
    /// The queue calls it beside its loop.</remarks>
    public void RecheckFolders()
    {
        var found = new Dictionary<StoredDownload, StoredDownload>();
        foreach (var download in Current.Downloads)
        {
            if (Recheck(download) is { } now && now != download)
            {
                found[download] = now;
            }
        }
        try
        {
            if (found.Count > 0)
            {
                Change(state => state with { Downloads = [.. state.Downloads.Select(d => found.GetValueOrDefault(d, d))] });
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            if (Current.FolderDeletes.Count > 0 || _indexDirty)
            {
                lock (_gate)
                {
                    if (FolderWork.IsCompleted)
                    {
                        FolderWork = FolderWorkAsync();
                    }
                }
            }
        }
    }

    /// <summary>
    /// The transfer of a removed download let go of its files: its folder, while still to be deleted, is tried again now, as one
    /// not tried yet, rather than once the wait the store's own tries left it with is over (a transfer slow to stop, on a busy
    /// PC, held its part past them). It goes beside the caller (<see cref="FolderWork"/>), after the index without it is on
    /// disk, as every removed folder does; one something else still holds waits again. The other folders keep their waits.
    /// </summary>
    internal void DeleteFolderNow(DownloadScope scope, string downloadId)
    {
        if (FolderBeingDeleted(scope, downloadId) is not null)
        {
            DeleteFoldersBeside(() => ForgetFolderWait(scope, downloadId));
        }
    }

    /// <summary>The bytes the scope's download folders take on their drives.</summary>
    public long BytesOnDisk(DownloadScope scope)
    {
        long total = 0;
        foreach (var download in In(scope).Where(d => !d.FilesMissing))
        {
            try
            {
                MeasuringFolder?.Invoke(download.Folder);
                total += new DirectoryInfo(download.Folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("The size of download {DownloadId} could not be read ({Failure})", download.Record.DownloadId, DownloadLog.Failure(ex));
            }
        }
        return total;
    }

    private async Task LoadOnceAsync()
    {
        var (loaded, write) = await Task.Run(ReadAll, CancellationToken.None);
        Change(_ => loaded);
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (write || loaded.FolderDeletes.Count > 0)
            {
                await WriteIndexThenDeleteFoldersAsync();
            }
        }
    }

    // After a removal: the queue hears first and stops the row's transfer, so its part is let go before the folder is deleted.
    // A handler that throws does not keep the change from being written; its exception comes after.
    private async Task RaiseThenPersistAsync()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            await WriteIndexThenDeleteFoldersAsync();
        }
    }

    // The removed downloads' folders, on the thread pool, after the folder work under way: that one deletes only the folders
    // of the index it wrote. beforeTry runs first in the pass's write turn.
    private void DeleteFoldersBeside(Action? beforeTry = null)
    {
        lock (_gate)
        {
            var before = FolderWork;
            FolderWork = before.IsCompleted
                ? Task.Run(() => FolderWorkAsync(beforeTry), CancellationToken.None)
                : before.ContinueWith(_ => FolderWorkAsync(beforeTry), CancellationToken.None, TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
        }
    }

    // The index and the folders beside a caller, as FolderWork: nothing may wait for it, so a failure is logged here. The
    // folders still to delete stay in the state, and the next recheck tries them again.
    private async Task FolderWorkAsync(Action? beforeTry = null)
    {
        try
        {
            await WriteIndexThenDeleteFoldersAsync(beforeTry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Deleting removed downloads' folders failed ({Failure}); they are tried again", DownloadLog.Failure(ex));
        }
    }

    // The index, then the removed downloads' folders. Changed follows when a folder went, so a server delete it held back goes;
    // a follower that throws then is logged, since the change it hears of is done already.
    private async Task WriteIndexThenDeleteFoldersAsync(Action? beforeTry = null)
    {
        if (!await PersistAsync(() =>
        {
            beforeTry?.Invoke();
            return WriteIndexThenDeleteFolders();
        }))
        {
            return;
        }
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("A follower of the downloads failed as a removed download's folder went ({Failure})", DownloadLog.Failure(ex));
        }
    }

    // What a row is now on disk: back from missing, damaged, or gone; itself when nothing changed.
    private StoredDownload Recheck(StoredDownload download)
    {
        bool there = Directory.Exists(download.Folder);
        if (!there)
        {
            return download.FilesMissing && !download.IsDamaged ? download : download with { FilesMissing = true, IsDamaged = false };
        }
        if (!download.FilesMissing)
        {
            return LookForMedia(download);
        }
        var (record, problem) = ReadRecord(download.Folder, download.Record.Scope, download.Record.DownloadId);
        if (record is not null)
        {
            return LookForMedia(download with { Record = record, FilesMissing = false, IsDamaged = false });
        }
        SayDamaged(download, problem);
        return download with { IsDamaged = true };
    }

    // Whether a finished download's media file is in its folder. Someone who deletes the file by hand, as to make room, leaves
    // the folder with its record behind: without this look the row would say Downloaded and offer Play.
    private static StoredDownload LookForMedia(StoredDownload download)
    {
        bool missing = download is { FilesMissing: false, Record.State: DownloadState.Completed, MediaPath: { } path } && !File.Exists(path);
        return missing == download.MediaMissing ? download : download with { MediaMissing = missing };
    }

    private Snapshot Change(Func<Snapshot, Snapshot> change)
    {
        lock (_gate)
        {
            return _state = change(_state);
        }
    }

    // Each write in turn, off the caller's thread; answers what the write answers (a folder found missing, or a folder deleted).
    private async Task<bool> PersistAsync(Func<bool> write)
    {
        await _writes.WaitAsync(CancellationToken.None);
        try
        {
            return await Task.Run(write, CancellationToken.None);
        }
        finally
        {
            _writes.Release();
        }
    }

    private static IReadOnlyList<StoredDownload> Replace(IReadOnlyList<StoredDownload> downloads, StoredDownload old, StoredDownload replacement) =>
        [.. downloads.Select(d => d == old ? replacement : d)];

    private static bool IsSameFolder(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a load's look for download folders did: the folders it listed, the records it read, whether it stopped early.</summary>
    internal sealed record ScanCount(int Folders, int Records, bool Capped);

    /// <summary>When a folder that could not be deleted is tried again, and how many tries in a row failed.</summary>
    private readonly record struct FolderRetry(DateTimeOffset At, int Failures);

    private sealed record Snapshot(IReadOnlyList<StoredDownload> Downloads, IReadOnlyList<PendingDelete> PendingDeletes,
        IReadOnlyList<FolderDelete> FolderDeletes)
    {
        public static readonly Snapshot Empty = new([], [], []);
    }
}
