using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.IO;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// What a sign-out or a server's removal took out of one of Granary's files, noted beside it (<c>&lt;file&gt;.removed</c>)
/// until the file is written without it (docs/architecture.md, "Signing out"). Such a file is never written over before it
/// was read, and a write can fail: without the note, a Granary that closed before the file could be written would find the
/// account's entries in it again at the next start.
/// </summary>
/// <remarks>
/// The file's owner reads the note before the file (<see cref="TryRead"/>) and leaves out what it covers
/// (<see cref="Covers"/>); it writes the note before the file (<see cref="Save"/>), names in the file the removals it is
/// written without (<see cref="Ids"/>), and lets the note go once the file is on the disk (<see cref="Done"/>). Each removal
/// has an id of its own, so a note that could not be deleted takes nothing out of a file that names it: what the same
/// account recorded after it signed in again stays, whatever the clock says. A write that ends without the file on the
/// disk says so (<see cref="NotWritten"/>): while a removal is in no note either, the note has the owner write again by
/// itself. Safe from any thread; the owner's writes, one at a time, call Save and then Done or NotWritten.
/// </remarks>
internal sealed class RemovalNote
{
    /// <summary>
    /// How long a removal that could be put neither in the note nor out of the file waits for the owner's next write
    /// (<see cref="NotWritten"/>).
    /// </summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);

    /// <summary>The longest such wait: each is twice the one before, up to this.</summary>
    public static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    private readonly string _path;
    private readonly string _what;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Action _writeAgain;
    private readonly Lock _gate = new();

    // Under the lock: the removals no write of the file is known to have followed, the note's and this run's.
    private readonly List<NotedRemoval> _removals = [];
    private bool _read;
    private bool _onDisk;
    private bool _behind;
    private bool _readFailureSaid;
    private bool _writeFailureSaid;

    // Under the lock: the write asked of the owner that waits for its time, the number of that try (CallOff), and how many
    // were asked for since a removal was first on no disk.
    private ITimer? _retry;
    private int _tryNumber;
    private int _retries;

    /// <param name="what">What the file holds, as a log line names it: "the positions recorded on this PC".</param>
    /// <param name="writeAgain">
    /// Has the owner write its file once more, as after a change (<see cref="NotWritten"/>). Called on a timer's thread,
    /// with no lock of the note's held.
    /// </param>
    public RemovalNote(string filePath, string what, ILogger logger, TimeProvider time, Action writeAgain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _path = filePath + ".removed";
        _what = what;
        _logger = logger;
        _time = time;
        _writeAgain = writeAgain;
    }

    /// <summary>A removal waits for the file to be written without it, or the note is still on the disk: the file is to be written.</summary>
    public bool Waits
    {
        get
        {
            lock (_gate)
            {
                return _onDisk || _removals.Count > 0;
            }
        }
    }

    /// <summary>
    /// Reads the note an earlier run left, once. False while it cannot be read (something has it open): it may name anyone,
    /// so the owner uses nothing of its file until it can be. A note that no longer says whose entries were to go covers
    /// everything the file holds: losing what was kept is the lesser harm than keeping what a sign-out was to take. So does
    /// one of a newer version than this build writes: it can no longer be understood.
    /// </summary>
    public bool TryRead()
    {
        lock (_gate)
        {
            if (_read)
            {
                return true;
            }
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(_path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                _read = true;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only the kind: the message would name the data folder, with the Windows user's name. Said once.
                if (!_readFailureSaid)
                {
                    _readFailureSaid = true;
                    _logger.LogWarning("The note of what a sign-out took out of {What} could not be read ({Failure}); their file is left alone until it can be",
                        _what, DownloadLog.Failure(ex));
                }
                return false;
            }
            // Before this run's own: they were removed first.
            _removals.InsertRange(0, Parse(bytes));
            _read = true;
            _onDisk = true;
            return true;
        }
    }

    // The note's removals. One that cannot be understood, damaged or of a newer version, covers everything.
    private IReadOnlyList<NotedRemoval> Parse(byte[] bytes)
    {
        try
        {
            var file = JsonSerializer.Deserialize(bytes, RemovalNoteJsonContext.Default.RemovalNoteFile);
            // A later Granary may mean more by its note than this one reads out of it, or something else: its removals
            // are not taken for this version's, though they may read as such. A note that names no version, or an
            // earlier one, is this version's: there is no earlier form.
            if (file is { Version: > RemovalNoteFile.CurrentVersion })
            {
                _logger.LogWarning("The note of what a sign-out took out of {What} is of a newer version than this Granary reads; everything their file holds is let go",
                    _what);
                return Everything(bytes);
            }
            if (file?.Removals is { } removals && removals.All(IsOneOfGranarys))
            {
                return removals;
            }
        }
        catch (JsonException)
        {
            // As a note that says nothing usable.
        }
        _logger.LogWarning("The note of what a sign-out took out of {What} is damaged; everything their file holds is let go", _what);
        return Everything(bytes);
    }

    // How the id of the removal that stands for everything begins.
    private const string EverythingPrefix = "damaged-";

    // Everything the file holds, under an id made of the note's content: a file written after it names that id, so the
    // same note, still there at the next start, takes nothing more.
    private static NotedRemoval[] Everything(byte[] bytes) =>
        [new NotedRemoval(EverythingPrefix + Convert.ToHexString(SHA256.HashData(bytes)), null, null)];

    // A removal as Save writes one; JSON can say null or anything for every member. One without a server is what a note
    // that could not be understood became (Everything), written into the note again by a later removal while that note
    // could not be deleted. It keeps its id: the file names that id, and under another one the note would take what was
    // recorded since. Any other removal without a server is not Granary's, and its note counts as damaged.
    private static bool IsOneOfGranarys(NotedRemoval? removal) => removal switch
    {
        { Id.Length: > 0, ServerEntryId.Length: > 0, AccountId: null or { Length: > 0 } } => true,
        { Id: { } id, ServerEntryId: null, AccountId: null } => id.StartsWith(EverythingPrefix, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Notes that an account's entries on a server were removed, or every account's there (<paramref name="accountId"/> null).</summary>
    public void Add(string serverEntryId, string? accountId)
    {
        lock (_gate)
        {
            _removals.Add(new NotedRemoval(Guid.NewGuid().ToString("N"), serverEntryId, accountId));
            _behind = true;
        }
    }

    /// <summary>
    /// An entry of that account on that server, read from the file, was removed and is to be left out.
    /// <paramref name="writtenAfter"/> are the removals the file names: it was written without those already, and what it
    /// holds was recorded since.
    /// </summary>
    public bool Covers(string serverEntryId, string accountId, IReadOnlyCollection<string>? writtenAfter)
    {
        lock (_gate)
        {
            return _removals.Any(r => (r.ServerEntryId is null || (r.ServerEntryId == serverEntryId && (r.AccountId is null || r.AccountId == accountId)))
                && writtenAfter?.Contains(r.Id) != true);
        }
    }

    /// <summary>The removals a file written now is without, for the file to name; null for none.</summary>
    public IReadOnlyList<string>? Ids()
    {
        lock (_gate)
        {
            return _removals.Count == 0 ? null : [.. _removals.Select(r => r.Id)];
        }
    }

    /// <summary>
    /// Writes the note when a removal is not in it yet, before the owner touches its file. One that cannot be written is
    /// logged, without whose or where: the removal then holds for this run only, until the file itself is written or a
    /// later write gets the note through (<see cref="NotWritten"/>).
    /// </summary>
    public void Save()
    {
        NotedRemoval[] removals;
        lock (_gate)
        {
            if (!_behind)
            {
                return;
            }
            // Never over a note that was not read: it names an earlier run's removals.
            if (!TryRead())
            {
                NotNoted("its note could not be read");
                return;
            }
            removals = [.. _removals];
            _behind = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new RemovalNoteFile { Removals = removals }, RemovalNoteJsonContext.Default.RemovalNoteFile);
            SafeFile.WriteWhole(_path, temp => File.WriteAllBytes(temp, json));
            lock (_gate)
            {
                _onDisk = true;
                _writeFailureSaid = false;
            }
        }
        catch (Exception ex)
        {
            // Whatever the disk does, the owner's write goes on. Only the kind, as above.
            lock (_gate)
            {
                _behind = true;
                NotNoted(DownloadLog.Failure(ex));
            }
        }
    }

    // Under the lock. Said once until a note is written again: every write of the file tries anew, and the file is
    // written again by itself until the removal is on the disk (NotWritten).
    private void NotNoted(string failure)
    {
        if (_writeFailureSaid)
        {
            return;
        }
        _writeFailureSaid = true;
        _logger.LogWarning("What a sign-out took out of {What} could not be noted on the disk ({Failure}); it is out for this run and tried again, and back at the next start unless the note or their file can be written before",
            _what, failure);
    }

    // Past this many doublings the wait is MaxRetry anyway.
    private const int MostDoublings = 10;

    /// <summary>
    /// The owner's write ended without its file on the disk. A removal that is in no note either is known to this run
    /// only, and nothing else may change the file before Granary closes, least of all after a sign-out. So the owner's
    /// write is asked for again: after <see cref="FirstRetry"/>, then twice as long each time up to
    /// <see cref="MaxRetry"/>, until the note or the file is on the disk. A removal that is noted asks for nothing: the
    /// next start leaves it out.
    /// </summary>
    public void NotWritten()
    {
        lock (_gate)
        {
            CallOff();
            if (!_behind)
            {
                _retries = 0;
                return;
            }
            var wait = TimeSpan.FromTicks(Math.Min(MaxRetry.Ticks, FirstRetry.Ticks << Math.Min(_retries, MostDoublings)));
            int number = _tryNumber;
            _retry = _time.CreateTimer(_ => Retry(number), null, wait, Timeout.InfiniteTimeSpan);
        }
    }

    // Under the lock: the try that waits is none any more. Disposing a timer does not take back a callback that is
    // already on its way to the thread pool, so each try has a number, and a callback that comes for another one than the
    // one waiting does nothing (Retry).
    private void CallOff()
    {
        _retry?.Dispose();
        _retry = null;
        _tryNumber++;
    }

    // On the timer's thread, for the try numbered so. One that was called off or set anew meanwhile asks for nothing: it
    // would bring the write before its time and count as a try that never came.
    private void Retry(int number)
    {
        lock (_gate)
        {
            if (number != _tryNumber)
            {
                return;
            }
            CallOff();
            _retries++;
        }
        _writeAgain();
    }

    /// <summary>
    /// The owner's file is on the disk without the removals <paramref name="ids"/> (<see cref="Ids"/>, as the file names
    /// them): the note goes, and they with it. A note that cannot be deleted stays with its removals, so every later write
    /// of the file names them too.
    /// </summary>
    public void Done(IReadOnlyCollection<string>? ids)
    {
        bool onDisk;
        lock (_gate)
        {
            onDisk = _onDisk;
            // The file is on the disk without them, whatever becomes of the note: no write is asked for again
            // (NotWritten). One removed while the file was written has a write of its own behind this one.
            CallOff();
            _retries = 0;
        }
        if (onDisk)
        {
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something has it open. The file names its removals, so it takes nothing more; the next write tries again.
                return;
            }
        }
        lock (_gate)
        {
            _onDisk = false;
            if (ids is not null)
            {
                _removals.RemoveAll(r => ids.Contains(r.Id));
            }
            // One removed while the file was written is in no note now.
            _behind = _removals.Count > 0;
        }
    }
}

/// <summary>
/// One removal a <see cref="RemovalNote"/> names: an account's entries on a server, or every account's there
/// (<see cref="AccountId"/> null). No <see cref="ServerEntryId"/> stands for everything, which only a note that cannot be
/// understood means; a later removal writes it into the note under the id it has.
/// </summary>
internal sealed record NotedRemoval(string Id, string? ServerEntryId, string? AccountId);

/// <summary><c>&lt;file&gt;.removed</c> as Granary writes it.</summary>
internal sealed record RemovalNoteFile
{
    /// <summary>The version this build writes and reads; a note of a later one is not understood (<see cref="RemovalNote.TryRead"/>).</summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public IReadOnlyList<NotedRemoval>? Removals { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(RemovalNoteFile))]
internal sealed partial class RemovalNoteJsonContext : JsonSerializerContext;
