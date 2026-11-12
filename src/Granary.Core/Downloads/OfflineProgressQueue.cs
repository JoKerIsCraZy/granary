using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.IO;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>Where a recorded position stands on its way to the server, in this run.</summary>
public enum OfflineProgressState
{
    /// <summary>Not sent yet, or sent without an answer that settles it: it goes with the next send.</summary>
    Pending,

    /// <summary>In a request that has not been answered.</summary>
    Sending,
}

/// <summary>What became of entries that were sent.</summary>
public enum OfflineProgressOutcome
{
    /// <summary>The server answered for them, or will never take them: they leave the queue.</summary>
    Done,

    /// <summary>
    /// The server did not answer for them, or asked to come back: they go with the next send. Sending a position twice changes
    /// nothing on the server, which keeps the later of two by the time each was recorded.
    /// </summary>
    Later,
}

/// <summary>A position a download played from this PC recorded, for the profile it was recorded as.</summary>
/// <param name="UpdatedAt">When it was recorded, in whole milliseconds: the server keeps the later of two positions.</param>
public sealed record OfflineProgressEntry(DownloadScope Scope, string MediaItemId, long PositionMs, long DurationMs, DateTimeOffset UpdatedAt,
    OfflineProgressState State);

/// <summary>
/// The positions recorded on this PC that the server has not taken yet (docs/architecture.md, "Offline progress"), in
/// <c>offline-progress.json</c> in the data folder: one for each title and profile, the latest. Modelled on the queue of
/// Silo's Apple app (<c>OfflineProgressQueue.swift</c>): a new position replaces the title's entry whatever its state, and a
/// send claims entries before it goes out. Unlike that queue it holds nothing back: an entry leaves only when the server
/// answered for it, will never take it, or is known to have it (<see cref="DropKnown"/>); every other one is sent again.
/// </summary>
/// <remarks>
/// Safe from any thread: every change is made under one lock and holds at once; the file follows, written whole off the
/// caller's thread, one write at a time, each with the state as it is then. The file is never written before it was read: one
/// that cannot be read now (something has it open) is read again before each write, and what was recorded meanwhile is kept
/// for this run and joins what the file holds; one that is not a queue is moved aside (<c>.bad</c>) first.
/// </remarks>
public sealed class OfflineProgressQueue
{
    /// <summary>An entry nobody could send for this long is let go when the file is read: its server or profile is gone.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    // A temporary file of a write (SafeFile) this old was left by a write that was cut off.
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(1);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger<OfflineProgressQueue> _logger;
    private readonly Lock _gate = new();

    // Under the lock. What the file held joins the entries once it is read (_loaded); until then they are this run's only,
    // and the scopes removed meanwhile are remembered for what the file still holds.
    private readonly List<OfflineProgressEntry> _entries = [];
    private readonly List<Func<DownloadScope, bool>> _removedUnread = [];
    private bool _loadTried;
    private bool _loaded;
    private bool _readFailureSaid;
    private Task _latestSave = Task.CompletedTask;

    public OfflineProgressQueue(string path, TimeProvider time, ILogger<OfflineProgressQueue> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _time = time;
        _logger = logger;
    }

    /// <summary><c>offline-progress.json</c> in the data folder.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "offline-progress.json");

    /// <summary>The write started most recently: done once the file is on the disk, or could not be written and was logged.</summary>
    public Task LatestSave
    {
        get
        {
            lock (_gate)
            {
                return _latestSave;
            }
        }
    }

    /// <summary>
    /// Reads the file on the thread pool, so the first use from the UI thread does not; done once it was read or could not be.
    /// </summary>
    public Task LoadAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            Loaded();
        }
    });

    /// <summary>
    /// Records where <paramref name="scope"/>'s profile is in a title now. It replaces the title's entry, also one on its
    /// way: the latest position is the one to send.
    /// </summary>
    public OfflineProgressEntry Record(DownloadScope scope, string mediaItemId, long positionMs, long durationMs)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaItemId);
        // Whole milliseconds, as the server keeps and answers them: the same instant compares equal when it is read back.
        var now = _time.GetUtcNow();
        var entry = new OfflineProgressEntry(scope, mediaItemId, Math.Max(0, positionMs), Math.Max(0, durationMs),
            new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, now.Offset), OfflineProgressState.Pending);
        lock (_gate)
        {
            var entries = Loaded();
            entries.RemoveAll(e => e.Scope == scope && e.MediaItemId == mediaItemId);
            entries.Add(entry);
            Save();
        }
        return entry;
    }

    /// <summary>The profile's entries, oldest first.</summary>
    public IReadOnlyList<OfflineProgressEntry> Entries(DownloadScope scope)
    {
        lock (_gate)
        {
            return [.. Loaded().Where(e => e.Scope == scope)];
        }
    }

    /// <summary>The profile recorded a position of the title here that the server has not answered for: waiting or on its way.</summary>
    public bool HasUnsent(DownloadScope scope, string mediaItemId)
    {
        lock (_gate)
        {
            return Loaded().Any(e => e.Scope == scope && e.MediaItemId == mediaItemId);
        }
    }

    /// <summary>
    /// Takes up to <paramref name="most"/> of the profile's pending entries for a send, oldest first: they are on their way
    /// from now on, and no other send takes them, until <see cref="Resolve"/> says what became of them. Only this run knows:
    /// after a Granary that closed meanwhile, they are pending again.
    /// </summary>
    public IReadOnlyList<OfflineProgressEntry> Claim(DownloadScope scope, int most)
    {
        lock (_gate)
        {
            var entries = Loaded();
            var claimed = new List<OfflineProgressEntry>();
            for (int index = 0; index < entries.Count && claimed.Count < most; index++)
            {
                if (entries[index].Scope == scope && entries[index].State == OfflineProgressState.Pending)
                {
                    entries[index] = entries[index] with { State = OfflineProgressState.Sending };
                    claimed.Add(entries[index]);
                }
            }
            return claimed;
        }
    }

    /// <summary>
    /// What became of entries a <see cref="Claim"/> handed out. One that a newer position replaced meanwhile is left alone:
    /// that position is another entry, still to send.
    /// </summary>
    public void Resolve(IReadOnlyCollection<OfflineProgressEntry> claimed, OfflineProgressOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        lock (_gate)
        {
            var entries = Loaded();
            bool left = false;
            foreach (var entry in claimed)
            {
                int index = entries.IndexOf(entry);
                if (index < 0)
                {
                    continue;
                }
                if (outcome == OfflineProgressOutcome.Done)
                {
                    entries.RemoveAt(index);
                    left = true;
                }
                else
                {
                    entries[index] = entry with { State = OfflineProgressState.Pending };
                }
            }
            if (left)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Lets go of the profile's pending entries the server is known to have: <paramref name="serverTimes"/> is when the
    /// server's progress in each title was recorded, as a complete read of it says, and a time at or after the entry's means
    /// the server has that position or a newer one, so a send would change nothing. Answers how many went.
    /// </summary>
    public int DropKnown(DownloadScope scope, IReadOnlyDictionary<string, DateTimeOffset> serverTimes)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(serverTimes);
        lock (_gate)
        {
            int gone = Loaded().RemoveAll(e => e.Scope == scope && e.State == OfflineProgressState.Pending
                && serverTimes.TryGetValue(e.MediaItemId, out var there) && there >= e.UpdatedAt);
            if (gone > 0)
            {
                Save();
            }
            return gone;
        }
    }

    /// <summary>
    /// Takes every entry of the scopes <paramref name="match"/> names out of the queue and its file, whatever its state: a
    /// sign-out that deletes an account's downloads, or a server's removal, leaves none of what was watched there behind.
    /// </summary>
    public void Remove(Func<DownloadScope, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        lock (_gate)
        {
            var entries = Loaded();
            if (!_loaded)
            {
                _removedUnread.Add(match);
            }
            if (entries.RemoveAll(e => match(e.Scope)) > 0 || !_loaded)
            {
                Save();
            }
        }
    }

    // Under the lock. The file is read at the first use (LoadAsync has the sync do that off the UI thread): a small file in
    // the data folder. One that could not be read then is read again by the next write, not by every caller.
    private List<OfflineProgressEntry> Loaded()
    {
        if (!_loadTried)
        {
            _loadTried = true;
            TryLoad();
        }
        return _entries;
    }

    // Under the lock. Sets _loaded once what the file holds is in _entries: no file, a file read, or one moved aside.
    private void TryLoad()
    {
        IReadOnlyList<OfflineProgressFileEntry> kept;
        try
        {
            kept = ReadFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something has it open, or it may not be read: its positions are still in it, so it is left as it is. Only the
            // kind: the message would name the data folder, with the Windows user's name. Said once: every write tries again.
            if (!_readFailureSaid)
            {
                _readFailureSaid = true;
                _logger.LogWarning("The positions recorded on this PC could not be read ({Failure}); the file is left alone and read again before each write",
                    DownloadLog.Failure(ex));
            }
            return;
        }
        var oldest = _time.GetUtcNow() - MaxAge;
        // JSON can say null or anything for every member. Every entry read is pending: one that was on its way when
        // Granary closed is sent again, which changes nothing on a server that has it.
        var read = kept
            .Where(e => e is { MediaItemId.Length: > 0, PositionMs: >= 0, DurationMs: >= 0 } && DownloadRecords.IsUsable(e.Scope) && e.UpdatedAt >= oldest
                && !_removedUnread.Any(removed => removed(e.Scope)))
            .Select(e => new OfflineProgressEntry(e.Scope, e.MediaItemId, e.PositionMs, e.DurationMs, e.UpdatedAt, OfflineProgressState.Pending));
        // What this run recorded before the file could be read is the newer of a title's two.
        var merged = read.Concat(_entries).GroupBy(e => (e.Scope, e.MediaItemId)).Select(group => group.MaxBy(e => e.UpdatedAt)!).ToList();
        _entries.Clear();
        _entries.AddRange(merged);
        _removedUnread.Clear();
        _loaded = true;
        SweepLeftovers();
    }

    // The file's entries; none when there is no file, or when it is not a queue and was moved aside. An I/O failure, also of
    // the move, is the caller's: the file stays unread.
    private IReadOnlyList<OfflineProgressFileEntry> ReadFile()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize(bytes, OfflineProgressJsonContext.Default.OfflineProgressFile)?.Entries ?? [];
        }
        catch (JsonException)
        {
            // Not written over: whoever wants to know what happened to it finds it beside the new file.
            File.Move(_path, _path + ".bad", overwrite: true);
            _logger.LogWarning("The file of positions recorded on this PC is damaged; it was moved aside (.bad) and the queue starts empty");
            return [];
        }
    }

    // A write that was cut off between its temporary file and the move left that file behind (SafeFile.WriteWhole). A young
    // one may be a write on its way, of this Granary or a second one on this data folder.
    private void SweepLeftovers()
    {
        try
        {
            var oldest = _time.GetUtcNow().UtcDateTime - LeftoverAge;
            string folder = Path.GetDirectoryName(_path)!;
            foreach (string leftover in Directory.EnumerateFiles(folder, Path.GetFileName(_path) + ".*.tmp"))
            {
                if (File.GetLastWriteTimeUtc(leftover) < oldest)
                {
                    File.Delete(leftover);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // They take a few bytes and get in no write's way.
        }
    }

    // Under the lock: the next write follows the one before, and writes the state as it is when its turn comes.
    private void Save() =>
        _latestSave = _latestSave.ContinueWith(_ => Write(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private void Write()
    {
        OfflineProgressFile file;
        lock (_gate)
        {
            if (!_loaded)
            {
                TryLoad();
            }
            if (!_loaded)
            {
                // Never over a file that was not read: it may hold positions this run does not know.
                return;
            }
            file = new OfflineProgressFile
            {
                Entries = [.. _entries.Select(e => new OfflineProgressFileEntry(e.Scope, e.MediaItemId, e.PositionMs, e.DurationMs, e.UpdatedAt))],
            };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(file, OfflineProgressJsonContext.Default.OfflineProgressFile);
            SafeFile.WriteWhole(_path, temp => File.WriteAllBytes(temp, json));
        }
        catch (Exception ex)
        {
            // Whatever the disk does, the chain of writes goes on and nobody who waits for one fails. Only the kind: the
            // message would name the data folder, with the Windows user's name.
            _logger.LogWarning("The positions recorded on this PC could not be written ({Failure}); they hold for this run", DownloadLog.Failure(ex));
        }
    }
}

/// <summary><c>offline-progress.json</c> as Granary writes it.</summary>
internal sealed record OfflineProgressFile
{
    public int Version { get; init; } = 1;

    public IReadOnlyList<OfflineProgressFileEntry> Entries { get; init => field = value ?? []; } = [];
}

/// <summary>
/// An entry as the file keeps it. Where it stands on its way is this run's alone: an earlier build's <c>state</c> member is
/// passed over, and every entry read is sent.
/// </summary>
internal sealed record OfflineProgressFileEntry(DownloadScope Scope, string MediaItemId, long PositionMs, long DurationMs, DateTimeOffset UpdatedAt);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(OfflineProgressFile))]
internal sealed partial class OfflineProgressJsonContext : JsonSerializerContext;
