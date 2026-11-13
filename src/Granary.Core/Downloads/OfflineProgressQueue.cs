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

/// <summary>The server's progress in a title as a complete read of it says: when it was recorded, and where.</summary>
public readonly record struct KnownProgress(DateTimeOffset UpdatedAt, long PositionMs);

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
/// for this run and joins what the file holds; one that is not a queue is moved aside (<c>.bad</c>) first. What a sign-out
/// takes out (<see cref="Remove"/>) is noted beside the file first (<see cref="RemovalNote"/>), so it also holds when the
/// file cannot be read or written before Granary closes.
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

    // Under the lock. What the file held joins the entries once it is read (_loaded); until then they are this run's only.
    private readonly List<OfflineProgressEntry> _entries = [];

    // What was removed and the file may still hold, this run's and an earlier one's.
    private readonly RemovalNote _note;
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
        _note = new RemovalNote(_path, "the positions recorded on this PC", logger);
    }

    /// <summary><c>offline-progress.json</c> in the data folder.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "offline-progress.json");

    /// <summary>For tests: runs on the write's thread before a write touches the disk, as a drive that does not answer holds it.</summary>
    internal Action? Writing { get; set; }

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
    /// <param name="after">
    /// A time this PC knows the title's progress to have had already: the last one it recorded or read from the server. The
    /// new position is later than that whatever the clock says, and is stamped so.
    /// </param>
    /// <remarks>
    /// The server keeps the later of two positions by their times. A PC whose clock was set back, or runs behind, would stamp
    /// a position recorded now before one it recorded or learned of earlier, and the server would pass the new one over. So a
    /// title's times never go backwards here: a position is stamped in a later whole second than the entry it replaces and
    /// than <paramref name="after"/>. A later millisecond would not do: a server on SQLite keeps whole seconds
    /// (<see cref="Has"/>), passes over a position of the second it has, and still answers success. A stamp moved on is the
    /// start of the next second, so positions of one title that follow each other within a second run a second ahead for
    /// each; a play records one every <see cref="LocalPlayback.ProgressInterval"/>, and the clock is past them again.
    /// </remarks>
    public OfflineProgressEntry Record(DownloadScope scope, string mediaItemId, long positionMs, long durationMs, DateTimeOffset? after = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaItemId);
        // Whole milliseconds, as the server keeps and answers them: the same instant compares equal when it is read back.
        var now = _time.GetUtcNow();
        var at = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, now.Offset);
        lock (_gate)
        {
            var entries = Loaded();
            var before = entries.Where(e => e.Scope == scope && e.MediaItemId == mediaItemId).Select(e => (DateTimeOffset?)e.UpdatedAt).Max();
            if ((before > after ? before : after ?? before) is { } known)
            {
                long nextSecond = known.UtcTicks - known.UtcTicks % TimeSpan.TicksPerSecond + TimeSpan.TicksPerSecond;
                if (at.UtcTicks < nextSecond)
                {
                    at = new DateTimeOffset(nextSecond, TimeSpan.Zero);
                }
            }
            var entry = new OfflineProgressEntry(scope, mediaItemId, Math.Max(0, positionMs), Math.Max(0, durationMs), at, OfflineProgressState.Pending);
            entries.RemoveAll(e => e.Scope == scope && e.MediaItemId == mediaItemId);
            entries.Add(entry);
            Save();
            return entry;
        }
    }

    /// <summary>The profile's entries, oldest first.</summary>
    public IReadOnlyList<OfflineProgressEntry> Entries(DownloadScope scope)
    {
        lock (_gate)
        {
            return [.. Loaded().Where(e => e.Scope == scope)];
        }
    }

    /// <summary>How many entries the scopes <paramref name="match"/> names still have here: titles whose progress is not on the server yet.</summary>
    public int Count(Func<DownloadScope, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        lock (_gate)
        {
            return Loaded().Count(e => match(e.Scope));
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
    /// Lets go of the profile's pending entries the server is known to have: <paramref name="server"/> is the server's
    /// progress in each title, as a complete read of it says. A time at or after the entry's means the server has that
    /// position or a newer one, so a send would change nothing. Answers how many went.
    /// </summary>
    public int DropKnown(DownloadScope scope, IReadOnlyDictionary<string, KnownProgress> server)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(server);
        lock (_gate)
        {
            int gone = Loaded().RemoveAll(e => e.Scope == scope && e.State == OfflineProgressState.Pending
                && server.TryGetValue(e.MediaItemId, out var there) && Has(there, e));
            if (gone > 0)
            {
                Save();
            }
            return gone;
        }
    }

    // The server has the entry's position, or a newer one. A server on SQLite keeps times in whole seconds
    // (silo-server/internal/userdb/progress.go formats them as RFC 3339 without a fraction, and compares that text), so the
    // entry's own row comes back up to a second older than the entry. A row at exactly the entry's second, on a whole
    // second, with the entry's very position is that entry; with any other position it may be an older one of the same
    // second, and the entry is sent, which costs a request and loses nothing.
    private static bool Has(KnownProgress there, OfflineProgressEntry entry) =>
        there.UpdatedAt >= entry.UpdatedAt
        || (there.UpdatedAt.Ticks % TimeSpan.TicksPerSecond == 0 && there.PositionMs == entry.PositionMs
            && there.UpdatedAt.UtcTicks == entry.UpdatedAt.UtcTicks - entry.UpdatedAt.UtcTicks % TimeSpan.TicksPerSecond);

    /// <summary>
    /// Takes every entry of an account on a server, or of every account there (<paramref name="accountId"/> null), out of
    /// the queue and its file, whatever its state: a sign-out that deletes an account's downloads, or a server's removal,
    /// leaves none of what was watched there behind.
    /// </summary>
    /// <remarks>
    /// The removal is noted beside the file before the file is written (<see cref="RemovalNote"/>), and the file is written
    /// whatever this run held: the file may hold more than the run does, as when it could not be read yet, an earlier write
    /// did not get through, or an entry was too old to be read in.
    /// </remarks>
    public void Remove(string serverEntryId, string? accountId)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverEntryId);
        lock (_gate)
        {
            var entries = Loaded();
            _note.Add(serverEntryId, accountId);
            entries.RemoveAll(e => e.Scope.ServerEntryId == serverEntryId && (accountId is null || e.Scope.AccountId == accountId));
            Save();
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
            // A note an earlier run left goes once the file is written without what it names.
            if (_note.Waits)
            {
                Save();
            }
        }
        return _entries;
    }

    // Under the lock. Sets _loaded once what the file holds is in _entries: no file, a file read, or one moved aside.
    private void TryLoad()
    {
        // The note first: what a sign-out removed is out of what the file holds before anything of it is shown or sent.
        // While the note cannot be read, the file counts as unread too.
        if (!_note.TryRead())
        {
            return;
        }
        OfflineProgressFile file;
        try
        {
            file = ReadFile();
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
        var read = file.Entries
            .Where(e => e is { MediaItemId.Length: > 0, PositionMs: >= 0, DurationMs: >= 0 } && DownloadRecords.IsUsable(e.Scope) && e.UpdatedAt >= oldest
                && !_note.Covers(e.Scope.ServerEntryId, e.Scope.AccountId, file.AfterRemovals))
            .Select(e => new OfflineProgressEntry(e.Scope, e.MediaItemId, e.PositionMs, e.DurationMs, e.UpdatedAt, OfflineProgressState.Pending));
        // What this run recorded before the file could be read is the newer of a title's two.
        var merged = read.Concat(_entries).GroupBy(e => (e.Scope, e.MediaItemId)).Select(group => group.MaxBy(e => e.UpdatedAt)!).ToList();
        _entries.Clear();
        _entries.AddRange(merged);
        _loaded = true;
        SweepLeftovers();
    }

    // The file; an empty one when there is none, or when it is not a queue and was moved aside. An I/O failure, also of the
    // move, is the caller's: the file stays unread.
    private OfflineProgressFile ReadFile()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new OfflineProgressFile();
        }
        try
        {
            return JsonSerializer.Deserialize(bytes, OfflineProgressJsonContext.Default.OfflineProgressFile) ?? new OfflineProgressFile();
        }
        catch (JsonException)
        {
            // Not written over: whoever wants to know what happened to it finds it beside the new file.
            File.Move(_path, _path + ".bad", overwrite: true);
            _logger.LogWarning("The file of positions recorded on this PC is damaged; it was moved aside (.bad) and the queue starts empty");
            return new OfflineProgressFile();
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
        Writing?.Invoke();
        // What a sign-out removed is noted before the file is touched: the file may not be readable now, or its write may
        // not get through, and Granary may close before either works.
        _note.Save();
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
                AfterRemovals = _note.Ids(),
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
            return;
        }
        _note.Done(file.AfterRemovals);
    }
}

/// <summary><c>offline-progress.json</c> as Granary writes it.</summary>
internal sealed record OfflineProgressFile
{
    public int Version { get; init; } = 1;

    public IReadOnlyList<OfflineProgressFileEntry> Entries { get; init => field = value ?? []; } = [];

    /// <summary>
    /// The removals of a sign-out's note (<see cref="RemovalNote"/>) this file was written without: a note still on the
    /// disk takes nothing more out of it. Null for none.
    /// </summary>
    public IReadOnlyList<string>? AfterRemovals { get; init; }
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
