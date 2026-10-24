using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Servers;

/// <summary>
/// <see cref="IServerRegistry"/> kept in a JSON file (<c>{ "active_server_id", "servers": [...] }</c>).
/// Changes are serialized by a gate and written atomically (temporary file, then replace); readers see
/// immutable snapshots. An unreadable file is set aside as <c>.bak</c> and the list starts empty.
/// <see cref="Changed"/> is raised on the thread that completed the change, which can be a pool thread.
/// </summary>
public sealed class JsonServerRegistry : IServerRegistry
{
    private const int IdLengthBytes = 8;

    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JsonServerRegistry> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot _state = Snapshot.Empty;
    private bool _loaded;

    public JsonServerRegistry(string filePath, TimeProvider timeProvider, ILogger<JsonServerRegistry> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _filePath = Path.GetFullPath(filePath);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public static string DefaultPath =>
        Path.Combine(AppPaths.DataDirectory, "servers.json");

    public IReadOnlyList<ServerEntry> Servers => _state.Servers;

    public string? ActiveServerId => _state.ActiveServerId;

    public ServerEntry? Active => _state.Active;

    public event EventHandler? Changed;

    /// <summary>
    /// The entry id for a server address: lower-case hex of the first 8 bytes of the SHA-256 of the UTF-8
    /// string <c>ServerAddress.Normalize(baseUri).OriginalString</c> (no trailing slash, for example
    /// <c>https://nas.example/silo</c>).
    /// </summary>
    public static string IdFor(Uri baseUri)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeAddress(baseUri).OriginalString));
        return Convert.ToHexStringLower(hash, 0, IdLengthBytes);
    }

    /// <summary>Reads the saved list. Later calls do nothing; changes made before it load the file first.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ServerEntry? Find(string id) => _state.Find(id);

    public Task<ServerEntry> AddOrUpdateAsync(Uri baseUri, string name, string? serverId, CancellationToken cancellationToken = default)
    {
        var address = NormalizeAddress(baseUri);
        string displayName = string.IsNullOrWhiteSpace(name) ? address.Host : name.Trim();
        return ChangeAsync(state =>
        {
            var now = _timeProvider.GetUtcNow();
            int index = state.IndexOfAddress(address);
            if (index >= 0)
            {
                var existing = state.Servers[index];
                var updated = existing with { Name = displayName, ServerId = serverId ?? existing.ServerId, LastUsedAt = now };
                return (state with { Servers = state.Servers.SetItem(index, updated) }, updated);
            }

            string id = IdFor(address);
            if (state.Find(id) is not null)
            {
                throw new InvalidOperationException($"Another saved server already uses the id {id}.");
            }
            var added = new ServerEntry(id, address, displayName, serverId, now, now, null);
            return (state with { Servers = state.Servers.Add(added) }, added);
        }, cancellationToken);
    }

    /// <summary>Replaces the entry with the same id. The address of a saved entry cannot change.</summary>
    public Task UpdateAsync(ServerEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var address = NormalizeAddress(entry.BaseUri);
        return ChangeAsync(state =>
        {
            int index = state.Servers.FindIndex(s => s.Id == entry.Id);
            if (index < 0)
            {
                _logger.LogDebug("Ignored an update of server {ServerEntryId}, which is no longer saved", entry.Id);
                return (state, false);
            }
            var current = state.Servers[index];
            if (!SameAddress(current.BaseUri, address))
            {
                throw new ArgumentException("The address of a saved server cannot change; add it as a new server instead.", nameof(entry));
            }
            var updated = entry with { BaseUri = current.BaseUri };
            return updated == current ? (state, false) : (state with { Servers = state.Servers.SetItem(index, updated) }, true);
        }, cancellationToken);
    }

    public Task SetActiveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ChangeAsync(state =>
        {
            int index = state.Servers.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                throw new ArgumentException($"No saved server has the id {id}.", nameof(id));
            }
            var used = state.Servers[index] with { LastUsedAt = _timeProvider.GetUtcNow() };
            return (new Snapshot(state.Servers.SetItem(index, used), id), true);
        }, cancellationToken);
    }

    /// <summary>Removes the entry, if it exists. Removing the active entry activates the most recently used remaining one.</summary>
    public Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ChangeAsync(state =>
        {
            int index = state.Servers.FindIndex(s => s.Id == id);
            if (index < 0)
            {
                return (state, false);
            }
            var remaining = state.Servers.RemoveAt(index);
            string? active = state.ActiveServerId == id
                ? remaining.MaxBy(s => s.LastUsedAt)?.Id
                : state.ActiveServerId;
            return (new Snapshot(remaining, active), true);
        }, cancellationToken);
    }

    private async Task<T> ChangeAsync<T>(Func<Snapshot, (Snapshot Next, T Result)> change, CancellationToken cancellationToken)
    {
        bool changed;
        T result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var current = _state;
            (var next, result) = change(current);
            changed = !ReferenceEquals(next, current);
            if (changed)
            {
                await SaveAsync(next, cancellationToken).ConfigureAwait(false);
                _state = next;
            }
        }
        finally
        {
            _gate.Release();
        }

        // Outside the gate, so handlers may call back into the registry.
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return result;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }
        _state = await ReadAsync(cancellationToken).ConfigureAwait(false);
        _loaded = true;
    }

    private async Task<Snapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return Snapshot.Empty;
        }
        ServerRegistryFile? file;
        try
        {
            file = await DeserializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            SetAsideUnreadableFile(ex);
            return Snapshot.Empty;
        }
        if (file is null)
        {
            SetAsideUnreadableFile(null);
            return Snapshot.Empty;
        }
        var (state, skipped) = FromFile(file);
        if (skipped > 0)
        {
            // The next save drops those entries; keep the original, since credentials are keyed by their ids.
            KeepBackup($"Skipped {skipped} invalid or duplicate server entries.");
        }
        return state;
    }

    private async Task<ServerRegistryFile?> DeserializeAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync(stream, ServerRegistryJsonContext.Default.ServerRegistryFile, cancellationToken).ConfigureAwait(false);
    }

    private void SetAsideUnreadableFile(Exception? error) =>
        KeepBackup("The server list is unreadable; starting with no servers.", error);

    private void KeepBackup(string reason, Exception? error = null)
    {
        string backup = _filePath + ".bak";
        try
        {
            File.Copy(_filePath, backup, overwrite: true);
            _logger.LogWarning(error, "{Reason} {Path} was copied to {Backup}", reason, _filePath, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{Reason} {Path} could not be copied to {Backup}", reason, _filePath, backup);
        }
    }

    private (Snapshot State, int Skipped) FromFile(ServerRegistryFile file)
    {
        var servers = ImmutableList.CreateBuilder<ServerEntry>();
        int skipped = 0;
        foreach (var stored in file.Servers ?? [])
        {
            var entry = stored is null ? null : ToEntry(stored);
            if (entry is null || servers.Exists(s => s.Id == entry.Id || SameAddress(s.BaseUri, entry.BaseUri)))
            {
                skipped++;
                continue;
            }
            servers.Add(entry);
        }
        string? active = file.ActiveServerId is { } id && servers.Exists(s => s.Id == id) ? id : null;
        return (new Snapshot(servers.ToImmutable(), active), skipped);
    }

    private static ServerEntry? ToEntry(ServerRegistryFileEntry stored)
    {
        if (!Uri.TryCreate(stored.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
        {
            return null;
        }
        Uri address;
        try
        {
            address = ServerAddress.Normalize(uri);
        }
        catch (UriFormatException)
        {
            return null;
        }
        // A stored id stays authoritative: credentials are keyed by it.
        string id = string.IsNullOrWhiteSpace(stored.Id) ? IdFor(address) : stored.Id;
        string name = string.IsNullOrWhiteSpace(stored.Name) ? address.Host : stored.Name;
        return new ServerEntry(id, address, name, stored.ServerId, stored.AddedAt, stored.LastUsedAt, stored.LastProfileId);
    }

    private async Task SaveAsync(Snapshot state, CancellationToken cancellationToken)
    {
        var file = new ServerRegistryFile(state.ActiveServerId, [.. state.Servers.Select(ToFileEntry)]);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string temp = _filePath + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, file, ServerRegistryJsonContext.Default.ServerRegistryFile, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _filePath, overwrite: true);
    }

    private static ServerRegistryFileEntry ToFileEntry(ServerEntry entry) =>
        new(entry.Id, entry.BaseUri.OriginalString, entry.Name, entry.ServerId, entry.AddedAt, entry.LastUsedAt, entry.LastProfileId);

    private static Uri NormalizeAddress(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A server address must be an absolute http or https URL.", nameof(baseUri));
        }
        return ServerAddress.Normalize(baseUri);
    }

    private static bool SameAddress(Uri normalized, Uri other) =>
        string.Equals(normalized.OriginalString, other.OriginalString, StringComparison.Ordinal);

    private sealed record Snapshot(ImmutableList<ServerEntry> Servers, string? ActiveServerId)
    {
        public static readonly Snapshot Empty = new(ImmutableList<ServerEntry>.Empty, null);

        public ServerEntry? Active => ActiveServerId is null ? null : Find(ActiveServerId);

        public ServerEntry? Find(string id) => Servers.Find(s => s.Id == id);

        public int IndexOfAddress(Uri normalized) => Servers.FindIndex(s => SameAddress(s.BaseUri, normalized));
    }
}
