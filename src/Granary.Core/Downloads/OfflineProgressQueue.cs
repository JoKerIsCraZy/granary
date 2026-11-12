using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.IO;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>Where a recorded position stands on its way to the server.</summary>
public enum OfflineProgressState
{
    /// <summary>Not sent yet, or sent back to wait: it goes with the next send.</summary>
    Pending,

    /// <summary>In a request that has not been answered.</summary>
    Sending,

    /// <summary>
    /// Sent without a usable answer, so the server may have taken it: never sent again by itself, as Silo's Apple app holds
    /// such an entry. A newer position of the title replaces it, and a read of the server's progress lets it go.
    /// </summary>
    Held,
}

/// <summary>What became of a batch of entries that was sent.</summary>
public enum OfflineProgressOutcome
{
    /// <summary>The server answered for them, or will never take them: they leave the queue.</summary>
    Done,

    /// <summary>They did not reach the server, or it asked to come back: they wait for the next send.</summary>
    Later,

    /// <summary>They left, and no usable answer came: they are held.</summary>
    Uncertain,
}

/// <summary>A position a download played from this PC recorded, for the profile it was recorded as.</summary>
/// <param name="UpdatedAt">When it was recorded, in whole milliseconds: the server keeps the later of two positions.</param>
public sealed record OfflineProgressEntry(DownloadScope Scope, string MediaItemId, long PositionMs, long DurationMs, DateTimeOffset UpdatedAt,
    OfflineProgressState State);

/// <summary>
/// The positions recorded on this PC that the server has not taken yet (docs/architecture.md, "Offline progress"), in
/// <c>offline-progress.json</c> in the data folder: one for each title and profile, the latest. Modelled on the queue of
/// Silo's Apple app (<c>OfflineProgressQueue.swift</c>): a new position replaces the title's entry whatever its state, a
/// send claims entries before it goes out, and an entry whose send got no answer is held instead of sent twice.
/// </summary>
/// <remarks>
/// Safe from any thread: every change is made under one lock and holds at once; the file follows, written whole off the
/// caller's thread, one write at a time, each with the state as it is then. A file that cannot be written is logged, and the
/// queue holds for this run.
/// </remarks>
public sealed class OfflineProgressQueue
{
    /// <summary>An entry nobody could send for this long is let go when the file is read: its server or profile is gone.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger<OfflineProgressQueue> _logger;
    private readonly Lock _gate = new();
    private List<OfflineProgressEntry>? _entries;
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
    /// Records where <paramref name="scope"/>'s profile is in a title now. It replaces the title's entry, also one on its way
    /// or held: the latest position is the one to send.
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

    /// <summary>The profile recorded a position of the title here that the server has not answered for: pending, on its way or held.</summary>
    public bool HasUnsent(DownloadScope scope, string mediaItemId)
    {
        lock (_gate)
        {
            return Loaded().Any(e => e.Scope == scope && e.MediaItemId == mediaItemId);
        }
    }

    /// <summary>
    /// Takes up to <paramref name="most"/> of the profile's pending entries for a send, oldest first: they are on their way
    /// from now on, and no other send takes them. Await <see cref="LatestSave"/> before they leave, so a Granary that closes
    /// meanwhile finds them held, not pending.
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
            if (claimed.Count > 0)
            {
                Save();
            }
            return claimed;
        }
    }

    /// <summary>
    /// What became of the entries a <see cref="Claim"/> handed out. One that a newer position replaced meanwhile is left
    /// alone: that position is another entry, still to send.
    /// </summary>
    public void Resolve(IReadOnlyList<OfflineProgressEntry> claimed, OfflineProgressOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        lock (_gate)
        {
            var entries = Loaded();
            bool changed = false;
            foreach (var entry in claimed)
            {
                int index = entries.IndexOf(entry);
                if (index < 0)
                {
                    continue;
                }
                changed = true;
                switch (outcome)
                {
                    case OfflineProgressOutcome.Done:
                        entries.RemoveAt(index);
                        break;
                    case OfflineProgressOutcome.Later:
                        entries[index] = entry with { State = OfflineProgressState.Pending };
                        break;
                    default:
                        entries[index] = entry with { State = OfflineProgressState.Held };
                        break;
                }
            }
            if (changed)
            {
                Save();
            }
        }
    }

    /// <summary>Lets the profile's held entries go: the server's progress was read, so what it has is known.</summary>
    public void DropHeld(DownloadScope scope)
    {
        lock (_gate)
        {
            if (Loaded().RemoveAll(e => e.Scope == scope && e.State == OfflineProgressState.Held) > 0)
            {
                Save();
            }
        }
    }

    // Under the lock. Read once, at the first use: a small file in the data folder, as this PC's settings are read.
    private List<OfflineProgressEntry> Loaded() => _entries ??= Read();

    private List<OfflineProgressEntry> Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(_path), OfflineProgressJsonContext.Default.OfflineProgressFile);
            var oldest = _time.GetUtcNow() - MaxAge;
            // JSON can say null or anything for every member. An entry that was on its way when Granary closed may have
            // arrived: it is held, as the Apple app holds one it finds dispatched at its start.
            return
            [
                .. (file?.Entries ?? [])
                    .Where(e => e is { MediaItemId.Length: > 0, PositionMs: >= 0, DurationMs: >= 0 } && DownloadRecords.IsUsable(e.Scope)
                        && Enum.IsDefined(e.State) && e.UpdatedAt >= oldest)
                    .Select(e => e.State == OfflineProgressState.Sending ? e with { State = OfflineProgressState.Held } : e)
                    .GroupBy(e => (e.Scope, e.MediaItemId)).Select(group => group.MaxBy(e => e.UpdatedAt)!),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("The positions recorded on this PC could not be read ({Failure}); none are kept from before", DownloadLog.Failure(ex));
            return [];
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
            file = new OfflineProgressFile { Entries = [.. Loaded()] };
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

    public IReadOnlyList<OfflineProgressEntry> Entries { get; init => field = value ?? []; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(OfflineProgressFile))]
internal sealed partial class OfflineProgressJsonContext : JsonSerializerContext;
