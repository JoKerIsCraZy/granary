using System.Globalization;
using System.Text.Json;
using Granary.Core.IO;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>The store's files: reading the index and the folders, writing records and the index, deleting folders.</summary>
public sealed partial class DownloadStore
{
    // The second folder level: "acct-" and the account's id (DownloadPaths.Folder).
    private const string AccountPrefix = "acct-";

    // A file a transfer or the player still holds a moment longer, as a cancelled transfer closes its part.
    private const int DeleteTries = 6;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(40);

    // A new download on disk, then in the state and the index. A delete still waiting for this very folder leaves the index
    // first: a crash after the folder is made cannot delete it at the next start. A server delete not yet sent for this very
    // download goes with it: the entry is this download's again, and the next index write, this one's, no longer names it.
    // Null, and nothing kept, when the scope's downloads were cleared since clearGeneration: before the folder is made, none
    // is made; while it is made, it goes again. The row kept carries the clears seen as it was added.
    private StoredDownload? AddToDisk(StoredDownload download, long clearGeneration)
    {
        var record = download.Record;
        if (IsClearedSince(record.Scope, clearGeneration))
        {
            return null;
        }
        var waiting = Current.FolderDeletes.Where(d => IsSameFolder(d.Folder, download.Folder)).ToList();
        var unsent = Current.PendingDeletes.Where(d => d.Scope == record.Scope && d.DownloadId == record.DownloadId).ToList();
        Change(state => state with
        {
            FolderDeletes = [.. state.FolderDeletes.Except(waiting)],
            PendingDeletes = [.. state.PendingDeletes.Except(unsent)],
        });
        try
        {
            if (waiting.Count > 0 && !WriteIndex(Current))
            {
                throw new IOException("The downloads index could not be written before the download's folder was made.");
            }
            MakeFolder(download);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept: what waited for this folder and this entry waits again.
            Change(state => state with { FolderDeletes = [.. state.FolderDeletes, .. waiting], PendingDeletes = [.. state.PendingDeletes, .. unsent] });
            throw;
        }
        // Checked with the change itself: a clear either comes first and keeps the row out, or after and takes it with the rest.
        StoredDownload? kept = null;
        Change(state =>
        {
            if (IsClearedSinceLocked(record.Scope, clearGeneration))
            {
                return state;
            }
            kept = download with { ClearsSeen = _clearGeneration };
            return state with { Downloads = [.. state.Downloads, kept] };
        });
        if (kept is null)
        {
            Unmake(download);
            return null;
        }
        WriteIndex(Current);
        return kept;
    }

    // A new download's folder starts empty: what a download before it under the same id left there is not its bytes. A folder
    // whose record cannot be written goes again, empty as it is.
    private void MakeFolder(StoredDownload download)
    {
        if (Directory.Exists(download.Folder))
        {
            _logger.LogInformation("Download {DownloadId} starts in a folder used before; its files go", download.Record.DownloadId);
            Directory.Delete(download.Folder, recursive: true);
        }
        Directory.CreateDirectory(download.Folder);
        try
        {
            FolderMade?.Invoke(download.Folder);
            WriteRecord(download);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The record of download {DownloadId} could not be written ({Failure}); its folder goes", download.Record.DownloadId,
                DownloadLog.Failure(ex));
            try
            {
                Directory.Delete(download.Folder, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("The new folder of download {DownloadId} stays empty ({Failure})", download.Record.DownloadId,
                    DownloadLog.Failure(cleanup));
            }
            throw;
        }
    }

    // The latest state of one download into its folder, and the index when the row's description changed or the index is
    // behind. Answers true when the folder turned out to be gone, which makes the row missing files.
    private bool WriteRecordOf(DownloadScope scope, string downloadId, bool indexChanged)
    {
        bool missing = false;
        // A row whose files are missing or damaged keeps the folder's own record: it is not overwritten with what the index knew.
        if (Find(scope, downloadId) is { FilesMissing: false } download)
        {
            try
            {
                WriteRecord(download);
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogInformation("The folder of download {DownloadId} is gone; its row stays as missing files", downloadId);
                Change(state => state with { Downloads = Replace(state.Downloads, download, download with { FilesMissing = true }) });
                missing = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("The record of download {DownloadId} could not be written ({Failure}); the change holds for this run", downloadId,
                    DownloadLog.Failure(ex));
            }
        }
        if (indexChanged || _indexDirty)
        {
            WriteIndex(Current);
        }
        return missing;
    }

    private static void WriteRecord(StoredDownload download)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(download.Record, DownloadStoreJsonContext.Default.DownloadRecord);
        SafeFile.WriteWhole(Path.Combine(download.Folder, DownloadPaths.Record), temp => File.WriteAllBytes(temp, json));
    }

    // Answers whether the index is on disk; one that is not stays due for every following write. An index the same as the one
    // last written is not written again.
    private bool WriteIndex(Snapshot state)
    {
        var index = new DownloadIndexFile
        {
            Downloads = [.. state.Downloads.Select(DownloadIndexEntry.Of)],
            PendingDeletes = state.PendingDeletes,
            FolderDeletes = state.FolderDeletes,
        };
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(index, DownloadStoreJsonContext.Default.DownloadIndexFile);
            if (!_indexDirty && _writtenIndex is { } written && json.AsSpan().SequenceEqual(written))
            {
                return true;
            }
            WritingIndex?.Invoke();
            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
            SafeFile.WriteWhole(_indexPath, temp => File.WriteAllBytes(temp, json));
            _writtenIndex = json;
            _indexDirty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _indexDirty = true;
            _logger.LogWarning("The downloads index could not be written ({Failure}); it is written again with the next change", DownloadLog.Failure(ex));
            return false;
        }
    }

    // Removed downloads' folders go only once the index without them is on disk: a folder deleted while the index still names it
    // would come back as missing files, and one kept while the index no longer names it would be taken in again. So the pass
    // deletes only the folders of the index it wrote, never one a remove since then added. Answers whether a folder's delete is
    // done.
    private bool WriteIndexThenDeleteFolders()
    {
        var written = Current;
        if (!WriteIndex(written))
        {
            return false;
        }
        IndexWritten?.Invoke();
        bool done = DeleteFolders(written.FolderDeletes);
        FoldersDeleted?.Invoke();
        if (done)
        {
            WriteIndex(Current);
        }
        return done;
    }

    // Each of these folders that is due, unless a row lives in that very folder again; answers whether any delete is done. One
    // that cannot go yet is tried again after a minute, then twice as long each time, up to MaxFolderDeleteWait: a drive that
    // stays away is not looked for every minute.
    private bool DeleteFolders(IReadOnlyList<FolderDelete> deletes)
    {
        bool done = false;
        var now = _time.GetUtcNow();
        foreach (var delete in deletes)
        {
            FolderRetry? retry;
            lock (_gate)
            {
                retry = _folderRetries.GetValueOrDefault(delete);
            }
            bool owned = Current.Downloads.Any(d => IsSameFolder(d.Folder, delete.Folder));
            if (!owned && delete.IsUsable && retry is { } waits && waits.At > now)
            {
                continue;
            }
            if (owned || !delete.IsUsable || TryDeleteFolder(delete))
            {
                Change(state => state with { FolderDeletes = [.. state.FolderDeletes.Where(d => d != delete)] });
                done = true;
                continue;
            }
            int failures = (retry?.Failures ?? 0) + 1;
            var wait = TimeSpan.FromTicks(Math.Min(MaxFolderDeleteWait.Ticks, FirstFolderDeleteWait.Ticks << Math.Min(failures - 1, 10)));
            lock (_gate)
            {
                _folderRetries[delete] = new FolderRetry(now + wait, failures);
            }
        }
        lock (_gate)
        {
            foreach (var gone in _folderRetries.Keys.Where(d => !_state.FolderDeletes.Contains(d)).ToList())
            {
                _folderRetries.Remove(gone);
            }
        }
        return done;
    }

    // True when the folder is gone; false while something holds it, or while its drive is not there to look.
    private bool TryDeleteFolder(FolderDelete delete)
    {
        for (int tries = 1; ; tries++)
        {
            try
            {
                if (!Directory.Exists(delete.Folder))
                {
                    return Path.GetPathRoot(delete.Folder) is { Length: > 0 } root && Directory.Exists(root);
                }
                Directory.Delete(delete.Folder, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (tries == DeleteTries)
                {
                    _logger.LogWarning("The folder of removed download {DownloadId} could not be deleted yet ({Failure}); it is tried again",
                        delete.DownloadId, DownloadLog.Failure(ex));
                    return false;
                }
                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }

    // The index as it was written, each folder read from its own record, and every download folder the index lacks (a write
    // that did not get through, an index that was lost) taken in. Answers whether the index needs writing.
    private (Snapshot State, bool Write) ReadAll()
    {
        Loading?.Invoke();
        var index = ReadIndexFile();
        bool write = false;
        var downloads = new List<StoredDownload>();
        var pending = new List<PendingDelete>();
        var folderDeletes = new List<FolderDelete>();
        if (index is not null)
        {
            foreach (var entry in index.Downloads)
            {
                if (TryLoad(entry, downloads) is not true)
                {
                    write = true;
                }
            }
            pending = [.. index.PendingDeletes.Where(DownloadRecords.IsUsable).Distinct()];
            folderDeletes = [.. index.FolderDeletes.Where(d => d is not null && d.IsUsable)];
            write |= pending.Count != index.PendingDeletes.Count || folderDeletes.Count != index.FolderDeletes.Count;
        }
        // Folders the index loaded, and removed downloads' folders still to delete, are not read again.
        var known = new HashSet<string>([.. downloads.Select(d => d.Folder), .. folderDeletes.Select(d => d.Folder)], StringComparer.OrdinalIgnoreCase);
        foreach (var found in FindFolders(known))
        {
            var record = found.Record;
            if (downloads.Any(d => d.Record.Scope == record.Scope && d.Record.DownloadId == record.DownloadId))
            {
                continue;
            }
            _logger.LogInformation("Download {DownloadId} was in its folder but not in the index; it is back", record.DownloadId);
            downloads.Add(found);
            write = true;
        }
        // A server delete for a download kept here is not sent: it was kept again after the delete was queued.
        int unsent = pending.Count;
        pending = [.. pending.Where(p => !downloads.Any(d => d.Record.Scope == p.Scope && d.Record.DownloadId == p.DownloadId))];
        write |= pending.Count != unsent;
        return (new Snapshot(downloads, pending, folderDeletes), write);
    }

    // One index entry as a row, unless it is damaged or there twice; null for one left out.
    private bool? TryLoad(DownloadIndexEntry? entry, List<StoredDownload> downloads)
    {
        try
        {
            if (entry is null || !entry.IsUsable)
            {
                _logger.LogWarning("An entry of the downloads index is damaged; it is left out");
                return null;
            }
            if (downloads.Any(d => d.Record.Scope == entry.Scope && d.Record.DownloadId == entry.DownloadId))
            {
                return null;
            }
            downloads.Add(Load(entry));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("An entry of the downloads index is damaged ({Failure}); it is left out", DownloadLog.Failure(ex));
            return null;
        }
    }

    private StoredDownload Load(DownloadIndexEntry entry)
    {
        if (!Directory.Exists(entry.Folder))
        {
            _logger.LogInformation("The folder of download {DownloadId} is missing; its row stays", entry.DownloadId);
            return new StoredDownload(entry.Placeholder(), entry.Folder, FilesMissing: true);
        }
        var (record, problem) = ReadRecord(entry.Folder, entry.Scope, entry.DownloadId);
        if (record is not null)
        {
            return LookForMedia(new StoredDownload(record, entry.Folder, FilesMissing: false));
        }
        var damaged = new StoredDownload(entry.Placeholder(), entry.Folder, FilesMissing: true) { IsDamaged = true };
        SayDamaged(damaged, problem);
        return damaged;
    }

    private void SayDamaged(StoredDownload download, string? problem)
    {
        lock (_gate)
        {
            if (!_damagedSaid.Add(download.Folder))
            {
                return;
            }
        }
        _logger.LogWarning("The record of download {DownloadId} is damaged ({Problem}); its row stays until it is deleted",
            download.Record.DownloadId, problem);
    }

    // The index as written, or null when there is none; one that cannot be read is set aside, and the folders make it again.
    private DownloadIndexFile? ReadIndexFile()
    {
        try
        {
            if (!File.Exists(_indexPath))
            {
                return null;
            }
            return JsonSerializer.Deserialize(File.ReadAllBytes(_indexPath), DownloadStoreJsonContext.Default.DownloadIndexFile)
                ?? throw new JsonException("The downloads index is empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning("The downloads index could not be read ({Failure}); it is made again from the download folders", DownloadLog.Failure(ex));
            SetAside();
            return null;
        }
    }

    // An index that could not be read stays beside, under the time it was found so, rather than being written over.
    private void SetAside()
    {
        string aside = _indexPath + "." + _time.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".unreadable";
        try
        {
            File.Move(_indexPath, aside, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("The unreadable downloads index stays where it is ({Failure})", DownloadLog.Failure(ex));
        }
    }

    // Every download.json at <root>\<server>\acct-<account>\<profile>\<download>\ under the downloads folder and the default one,
    // which is "downloads" in the data folder the index is in (DownloadPaths.DefaultRoot for the app's own index), except in the
    // folders already known. The downloads folder can be any folder, a media library among them: the look goes only into names
    // Granary's layout makes, and lists at most ScanLimit folders.
    private List<StoredDownload> FindFolders(HashSet<string> known)
    {
        var found = new List<StoredDownload>();
        var scan = new Scan(ScanLimit);
        foreach (string root in Roots())
        {
            foreach (string folder in DownloadFolders(root, scan).Where(f => !known.Contains(f)))
            {
                scan.Records++;
                var (record, _) = ReadRecord(folder, null, null);
                if (record is not null && DownloadRecords.IsOwnFolder(folder, record.Scope, record.DownloadId)
                    && !found.Any(d => d.Record.Scope == record.Scope && d.Record.DownloadId == record.DownloadId))
                {
                    found.Add(LookForMedia(new StoredDownload(record, folder, FilesMissing: false)));
                }
            }
        }
        if (scan.Capped)
        {
            _logger.LogWarning("The look for download folders stopped after {Limit} folders; a download further on comes back once it is listed again",
                scan.Limit);
        }
        LastScan = new ScanCount(scan.Folders, scan.Records, scan.Capped);
        return found;
    }

    // The folders four levels under root whose names Granary's layout makes: a server's, "acct-" and an account's, a profile's
    // and a download's.
    private List<string> DownloadFolders(string root, Scan scan)
    {
        List<string> level = [root];
        for (int depth = 1; depth <= 4; depth++)
        {
            level = [.. level.SelectMany(folder => SubfoldersOf(folder, scan)).Where(folder => IsLayoutName(Path.GetFileName(folder), depth))];
        }
        return level;
    }

    private static bool IsLayoutName(string name, int depth) =>
        depth == 2
            ? name.StartsWith(AccountPrefix, StringComparison.Ordinal) && DownloadPaths.IsSegment(name[AccountPrefix.Length..])
            : DownloadPaths.IsSegment(name);

    private IEnumerable<string> Roots()
    {
        var roots = new List<string>();
        foreach (string root in new[] { _preferences.Folder, Path.Combine(Path.GetDirectoryName(_indexPath)!, "downloads") })
        {
            try
            {
                roots.Add(Path.GetFullPath(root));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _logger.LogDebug("A downloads folder is no path ({Failure})", DownloadLog.Failure(ex));
            }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // A folder's subfolders, counted against the scan's limit; none once it is reached.
    private string[] SubfoldersOf(string folder, Scan scan)
    {
        if (scan.Folders >= scan.Limit)
        {
            scan.Capped = true;
            return [];
        }
        try
        {
            if (!Directory.Exists(folder))
            {
                return [];
            }
            scan.Folders++;
            return Directory.GetDirectories(folder);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("A folder under the downloads folder could not be read ({Failure})", DownloadLog.Failure(ex));
            return [];
        }
    }

    // One load's look for download folders as it goes.
    private sealed class Scan(int limit)
    {
        public int Limit { get; } = limit;

        public int Folders { get; set; }

        public int Records { get; set; }

        public bool Capped { get; set; }
    }

    // A folder's own record, cleaned, when it is the download expected there (any, for null); else null and why.
    private static (DownloadRecord? Record, string? Problem) ReadRecord(string folder, DownloadScope? scope, string? downloadId)
    {
        try
        {
            string path = Path.Combine(folder, DownloadPaths.Record);
            if (!File.Exists(path))
            {
                return (null, "no record");
            }
            var record = DownloadRecords.Clean(JsonSerializer.Deserialize(File.ReadAllBytes(path), DownloadStoreJsonContext.Default.DownloadRecord));
            if (record is null)
            {
                return (null, "a value is missing or impossible");
            }
            return scope is null || (record.Scope == scope && record.DownloadId == downloadId) ? (record, null) : (null, "another download's record");
        }
        catch (Exception ex)
        {
            return (null, DownloadLog.Failure(ex));
        }
    }
}
