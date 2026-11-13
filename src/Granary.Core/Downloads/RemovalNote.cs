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
/// account recorded after it signed in again stays, whatever the clock says. Safe from any thread; the owner's writes, one
/// at a time, call Save and Done.
/// </remarks>
internal sealed class RemovalNote
{
    private readonly string _path;
    private readonly string _what;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    // Under the lock: the removals no write of the file is known to have followed, the note's and this run's.
    private readonly List<NotedRemoval> _removals = [];
    private bool _read;
    private bool _onDisk;
    private bool _behind;
    private bool _readFailureSaid;
    private bool _writeFailureSaid;

    /// <param name="what">What the file holds, as a log line names it: "the positions recorded on this PC".</param>
    public RemovalNote(string filePath, string what, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _path = filePath + ".removed";
        _what = what;
        _logger = logger;
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
            // JSON can say null or anything for every member.
            if (file?.Removals is { } removals
                && removals.All(r => r is { Id.Length: > 0, ServerEntryId.Length: > 0, AccountId: null or { Length: > 0 } }))
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

    // Everything the file holds, under an id made of the note's content: a file written after it names that id, so the
    // same note, still there at the next start, takes nothing more.
    private static NotedRemoval[] Everything(byte[] bytes) => [new NotedRemoval("damaged-" + Convert.ToHexString(SHA256.HashData(bytes)), null, null)];

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
    /// logged, without whose or where: the removal then holds for this run only, until the file itself is written.
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

    // Under the lock. Said once until a note is written again: every write of the file tries anew.
    private void NotNoted(string failure)
    {
        if (_writeFailureSaid)
        {
            return;
        }
        _writeFailureSaid = true;
        _logger.LogWarning("What a sign-out took out of {What} could not be noted on the disk ({Failure}); it is out for this run, and back at the next start unless their file can be written before",
            _what, failure);
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
/// understood means.
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
