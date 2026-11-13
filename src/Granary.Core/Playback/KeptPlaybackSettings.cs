using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
using Granary.Core.Downloads;
using Granary.Core.IO;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>
/// The playback settings as the server last answered them, kept on this PC for each server, account and profile
/// (<c>playback-settings.json</c> in the data folder), so a download played without the server starts with the profile's own
/// subtitle look, speed, skips and marker settings instead of the defaults (docs/architecture.md, "Playing a download"). What
/// is kept is the server's answer to each batch of keys <see cref="PlaybackPreferencesReader"/> asks, whole, so a kept batch
/// is read exactly as a fresh one.
/// </summary>
/// <remarks>
/// Safe from any thread. The file is read at the first use and written whole, off the caller's thread, only when an answer
/// differs from the one kept. It is a copy of what the server has: one that cannot be read or written is logged, and playback
/// goes on without it. It is never written before it was read: a file that cannot be read now (something has it open) holds
/// the other profiles' settings, so each write tries the read again first, and what was kept meanwhile joins what it holds.
/// What a sign-out forgets (<see cref="Remove"/>) is noted beside the file first (<see cref="RemovalNote"/>), so it also
/// holds when the file cannot be read or written before Granary closes.
/// </remarks>
public sealed class KeptPlaybackSettings
{
    private readonly string _path;
    private readonly ISessionManager _session;
    private readonly ILogger<KeptPlaybackSettings> _logger;
    private readonly DownloadStore? _downloads;
    private readonly Lock _gate = new();

    // Under the lock: each owner's batches by their keys, with the JSON they were kept as, to tell a change. What the file
    // held joins them once it is read (_loaded); until then they are this run's only.
    private readonly Dictionary<string, Dictionary<string, Kept>> _owners = new(StringComparer.Ordinal);

    // What was forgotten and the file may still hold, this run's and an earlier one's.
    private readonly RemovalNote _note;
    private bool _loadTried;
    private bool _loaded;
    private bool _readFailureSaid;
    private Task _latestSave = Task.CompletedTask;
    private bool _saveWaits;

    /// <param name="downloads">
    /// Says which accounts are being signed out with their downloads (<see cref="DownloadStore.IsClearPending"/>): nothing is
    /// kept for those meanwhile.
    /// </param>
    public KeptPlaybackSettings(string path, ISessionManager session, ILogger<KeptPlaybackSettings> logger, DownloadStore? downloads = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _session = session;
        _logger = logger;
        _downloads = downloads;
        _note = new RemovalNote(_path, "the playback settings kept on this PC", logger);
    }

    /// <summary><c>playback-settings.json</c> in the data folder.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "playback-settings.json");

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
    /// Whose settings the session reads now: its server's entry, account and profile; null unless it is ready with all three.
    /// A reader takes it before it asks the server and keeps the answer only if it is still the same.
    /// </summary>
    public string? Owner() =>
        _session.State == SessionState.Ready && _session.ActiveServer is { } server && _session.Account is { } account
        && _session.ProfileId is { Length: > 0 } profile
            ? Key(server.Id, account.Id) + profile
            : null;

    /// <summary>
    /// Keeps the server's answer to a batch of keys for <paramref name="owner"/> (<see cref="Owner"/>), unless its account is
    /// being signed out with its downloads: the sign-out forgets what is kept (<see cref="Remove"/>) while the account is
    /// still signed in, and an answer that comes before the sign-in ends would keep it anew.
    /// </summary>
    public void Keep(string owner, IReadOnlyList<string> keys, EffectiveSettings values)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(values);
        string batch = string.Join(',', keys);
        string json = JsonSerializer.Serialize(values, KeptSettingsJsonContext.Default.EffectiveSettings);
        lock (_gate)
        {
            // Asked under the lock: the sign-out marks its clear before it forgets, so an answer that saw no clear is
            // kept before the forgetting, and goes with it.
            if (IsBeingSignedOut(owner))
            {
                return;
            }
            var owners = Loaded();
            if (!owners.TryGetValue(owner, out var batches))
            {
                owners[owner] = batches = new Dictionary<string, Kept>(StringComparer.Ordinal);
            }
            if (batches.TryGetValue(batch, out var kept) && kept.Json == json)
            {
                return;
            }
            batches[batch] = new Kept(values, json);
            Save();
        }
    }

    /// <summary>The answer last kept for <paramref name="owner"/>'s batch of keys; null when none was.</summary>
    public EffectiveSettings? Find(string owner, IReadOnlyList<string> keys)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentNullException.ThrowIfNull(keys);
        lock (_gate)
        {
            return Loaded().TryGetValue(owner, out var batches) && batches.TryGetValue(string.Join(',', keys), out var kept) ? kept.Values : null;
        }
    }

    /// <summary>
    /// Forgets what is kept for an account on a server, or for every account there (<paramref name="accountId"/> null): a
    /// sign-out the viewer asked for, or the server's removal.
    /// </summary>
    /// <remarks>
    /// The removal is noted beside the file before the file is written (<see cref="RemovalNote"/>), and the file is written
    /// whatever this run held: the file may hold more than the run does, as when it could not be read yet or an earlier
    /// write did not get through.
    /// </remarks>
    public void Remove(string serverEntryId, string? accountId)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverEntryId);
        string prefix = accountId is null ? serverEntryId + "/" : Key(serverEntryId, accountId);
        lock (_gate)
        {
            var owners = Loaded();
            _note.Add(serverEntryId, accountId);
            foreach (string owner in owners.Keys.Where(owner => owner.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                owners.Remove(owner);
            }
            Save();
        }
    }

    // Ids are the server's and Granary's own and hold no slash.
    private static string Key(string serverEntryId, string accountId) => serverEntryId + "/" + accountId + "/";

    // The owner's account is being signed out, or its server removed, and the downloads go with it (DownloadSignOut): until
    // that ended, whether it signed the account out or failed.
    private bool IsBeingSignedOut(string owner) =>
        _downloads is not null && owner.Split('/') is [var server, var account, var profile]
        && _downloads.IsClearPending(new DownloadScope(server, account, profile));

    // Under the lock. The file is read at the first use; one that could not be read then is read again by the next write.
    private Dictionary<string, Dictionary<string, Kept>> Loaded()
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
        return _owners;
    }

    // Under the lock. Sets _loaded once what the file holds is in _owners: no file, a file read, or one that is no such file.
    private void TryLoad()
    {
        // The note first: what a sign-out forgot is out of what the file holds before a play can start with it. While the
        // note cannot be read, the file counts as unread too.
        if (!_note.TryRead())
        {
            return;
        }
        KeptSettingsFile? file = null;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllBytes(_path), KeptSettingsJsonContext.Default.KeptSettingsFile);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing kept yet.
        }
        catch (JsonException)
        {
            _logger.LogWarning("The playback settings kept on this PC are damaged; they are kept anew");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Its settings are still in it: it is left as it is. Only the kind: the message would name the data folder, with
            // the Windows user's name. Said once: every write tries again.
            if (!_readFailureSaid)
            {
                _readFailureSaid = true;
                _logger.LogWarning("The playback settings kept on this PC could not be read ({Failure}); the file is left alone and read again before each write",
                    ex.GetType().Name);
            }
            return;
        }
        // JSON can say null for every member, and anything for an owner. What this run kept before the file could be read is
        // the newer of a batch's two.
        foreach (var (owner, batches) in file?.Profiles ?? [])
        {
            if (owner?.Split('/') is not [{ Length: > 0 } server, { Length: > 0 } account, { Length: > 0 }] || batches is null
                || _note.Covers(server, account, file?.AfterRemovals))
            {
                continue;
            }
            if (!_owners.TryGetValue(owner, out var kept))
            {
                _owners[owner] = kept = new Dictionary<string, Kept>(StringComparer.Ordinal);
            }
            foreach (var (batch, values) in batches)
            {
                if (batch is { Length: > 0 } && values is { Items: not null } && values.Items.All(i => i is { Key.Length: > 0 }))
                {
                    kept.TryAdd(batch, new Kept(values, JsonSerializer.Serialize(values, KeptSettingsJsonContext.Default.EffectiveSettings)));
                }
            }
        }
        _loaded = true;
    }

    // Under the lock. A start reads several batches in a row: the write that waits takes them all.
    private void Save()
    {
        if (_saveWaits)
        {
            return;
        }
        _saveWaits = true;
        _latestSave = _latestSave.ContinueWith(_ => Write(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void Write()
    {
        // A change from here on starts a write of its own.
        lock (_gate)
        {
            _saveWaits = false;
        }
        // What a sign-out forgot is noted before the file is touched: the file may not be readable now, or its write may
        // not get through, and Granary may close before either works.
        _note.Save();
        KeptSettingsFile file;
        lock (_gate)
        {
            if (!_loaded)
            {
                TryLoad();
            }
            if (!_loaded)
            {
                // Never over a file that was not read: it holds settings this run does not know.
                return;
            }
            file = new KeptSettingsFile
            {
                Profiles = Loaded().ToDictionary(owner => owner.Key,
                    owner => owner.Value.ToDictionary(batch => batch.Key, batch => batch.Value.Values, StringComparer.Ordinal), StringComparer.Ordinal),
                AfterRemovals = _note.Ids(),
            };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(file, KeptSettingsJsonContext.Default.KeptSettingsFile);
            SafeFile.WriteWhole(_path, temp => File.WriteAllBytes(temp, json));
        }
        catch (Exception ex)
        {
            // Whatever the disk does, the chain of writes goes on. Only the kind, as above.
            _logger.LogWarning("The playback settings could not be kept on this PC ({Failure})", ex.GetType().Name);
            return;
        }
        _note.Done(file.AfterRemovals);
    }

    private sealed record Kept(EffectiveSettings Values, string Json);
}

/// <summary><c>playback-settings.json</c> as Granary writes it: each owner's batches of keys, as the server answered them.</summary>
internal sealed record KeptSettingsFile
{
    public int Version { get; init; } = 1;

    public Dictionary<string, Dictionary<string, EffectiveSettings>> Profiles { get; init; } = [];

    /// <summary>
    /// The removals of a sign-out's note (<see cref="RemovalNote"/>) this file was written without: a note still on the
    /// disk takes nothing more out of it. Null for none.
    /// </summary>
    public IReadOnlyList<string>? AfterRemovals { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(KeptSettingsFile))]
[JsonSerializable(typeof(EffectiveSettings))]
internal sealed partial class KeptSettingsJsonContext : JsonSerializerContext;
