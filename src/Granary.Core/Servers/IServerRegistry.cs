namespace Granary.Core.Servers;

/// <summary>
/// A saved server address. <see cref="Id"/> identifies the saved address (the first 8 bytes of the
/// SHA-256 of the normalized base URL, lower-case hex), not the deployment: two addresses of the same
/// deployment are two entries that share <see cref="ServerId"/>. Credentials are stored per <see cref="Id"/>.
/// </summary>
public sealed record ServerEntry(
    string Id,
    Uri BaseUri,
    string Name,
    string? ServerId,
    DateTimeOffset AddedAt,
    DateTimeOffset LastUsedAt,
    string? LastProfileId);

/// <summary>
/// The saved servers and which one is active (docs/api-contract.md, 6.5). Holds no secrets; tokens live
/// in the secret store. Implementations persist every change before the returned task completes.
/// </summary>
public interface IServerRegistry
{
    IReadOnlyList<ServerEntry> Servers { get; }

    string? ActiveServerId { get; }

    ServerEntry? Active { get; }

    /// <summary>Raised after any change (add, update, remove, activation).</summary>
    event EventHandler? Changed;

    /// <summary>Loads the saved list; call once at startup.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    ServerEntry? Find(string id);

    /// <summary>Adds a server, or updates name and server id of the entry with the same normalized URL.</summary>
    Task<ServerEntry> AddOrUpdateAsync(Uri baseUri, string name, string? serverId, CancellationToken cancellationToken = default);

    Task UpdateAsync(ServerEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Makes the entry active and bumps its <see cref="ServerEntry.LastUsedAt"/>.</summary>
    Task SetActiveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Removes the entry. If it was active, the most recently used remaining entry becomes active.</summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);
}
