using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.Api.Settings;
using Granary.Core.Auth;
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
/// goes on without it.
/// </remarks>
public sealed class KeptPlaybackSettings
{
    private readonly string _path;
    private readonly ISessionManager _session;
    private readonly ILogger<KeptPlaybackSettings> _logger;
    private readonly Lock _gate = new();

    // Under the lock: each owner's batches by their keys, with the JSON they were kept as, to tell a change.
    private Dictionary<string, Dictionary<string, Kept>>? _owners;
    private Task _latestSave = Task.CompletedTask;
    private bool _saveWaits;

    public KeptPlaybackSettings(string path, ISessionManager session, ILogger<KeptPlaybackSettings> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _session = session;
        _logger = logger;
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

    /// <summary>Keeps the server's answer to a batch of keys for <paramref name="owner"/> (<see cref="Owner"/>).</summary>
    public void Keep(string owner, IReadOnlyList<string> keys, EffectiveSettings values)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(values);
        string batch = string.Join(',', keys);
        string json = JsonSerializer.Serialize(values, KeptSettingsJsonContext.Default.EffectiveSettings);
        lock (_gate)
        {
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
    public void Remove(string serverEntryId, string? accountId)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverEntryId);
        string prefix = accountId is null ? serverEntryId + "/" : Key(serverEntryId, accountId);
        lock (_gate)
        {
            var owners = Loaded();
            var gone = owners.Keys.Where(owner => owner.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (gone.Count == 0)
            {
                return;
            }
            foreach (string owner in gone)
            {
                owners.Remove(owner);
            }
            Save();
        }
    }

    // Ids are the server's and Granary's own and hold no slash.
    private static string Key(string serverEntryId, string accountId) => serverEntryId + "/" + accountId + "/";

    // Under the lock.
    private Dictionary<string, Dictionary<string, Kept>> Loaded() => _owners ??= Read();

    private Dictionary<string, Dictionary<string, Kept>> Read()
    {
        var owners = new Dictionary<string, Dictionary<string, Kept>>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path))
            {
                return owners;
            }
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(_path), KeptSettingsJsonContext.Default.KeptSettingsFile);
            // JSON can say null for every member.
            foreach (var (owner, batches) in file?.Profiles ?? [])
            {
                if (owner is not { Length: > 0 } || batches is null)
                {
                    continue;
                }
                var kept = new Dictionary<string, Kept>(StringComparer.Ordinal);
                foreach (var (batch, values) in batches)
                {
                    if (batch is { Length: > 0 } && values is { Items: not null } && values.Items.All(i => i is { Key.Length: > 0 }))
                    {
                        kept[batch] = new Kept(values, JsonSerializer.Serialize(values, KeptSettingsJsonContext.Default.EffectiveSettings));
                    }
                }
                owners[owner] = kept;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Only the kind: the message would name the data folder, with the Windows user's name.
            _logger.LogWarning("The playback settings kept on this PC could not be read ({Failure}); they are kept anew", ex.GetType().Name);
            owners.Clear();
        }
        return owners;
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
        KeptSettingsFile file;
        lock (_gate)
        {
            _saveWaits = false;
            file = new KeptSettingsFile
            {
                Profiles = Loaded().ToDictionary(owner => owner.Key,
                    owner => owner.Value.ToDictionary(batch => batch.Key, batch => batch.Value.Values, StringComparer.Ordinal), StringComparer.Ordinal),
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
        }
    }

    private sealed record Kept(EffectiveSettings Values, string Json);
}

/// <summary><c>playback-settings.json</c> as Granary writes it: each owner's batches of keys, as the server answered them.</summary>
internal sealed record KeptSettingsFile
{
    public int Version { get; init; } = 1;

    public Dictionary<string, Dictionary<string, EffectiveSettings>> Profiles { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(KeptSettingsFile))]
[JsonSerializable(typeof(EffectiveSettings))]
internal sealed partial class KeptSettingsJsonContext : JsonSerializerContext;
