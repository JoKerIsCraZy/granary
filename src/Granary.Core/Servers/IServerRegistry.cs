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
    string? LastProfileId)
{
    /// <summary>What the server's branding last answered; null until it answered once.</summary>
    public SavedBranding? Branding { get; init; }
}

/// <summary>
/// The server's branding as it last answered (<c>GET /api/v2/theme/branding</c>), so Granary paints its accent at once
/// the next time, before the server answers, and keeps it while a read fails, as Silo's apps keep the last branding of
/// each server. <see cref="Accent"/> is the accent as "#rrggbb", or <see cref="NoAccent"/> for a server without one;
/// <see cref="Wordmark"/> the wordmark's address on the server, or null.
/// </summary>
public sealed record SavedBranding(string Accent, string? Wordmark)
{
    public const string NoAccent = "none";
}

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

    /// <summary>
    /// Adds a server, or updates name and server id of the entry with the same normalized URL. A session saved for that
    /// entry is the caller's to settle first when the id changes (<see cref="Auth.ISessionManager.ReopenServerAsync"/>):
    /// a session of one server must not stay on an entry that now names another.
    /// </summary>
    Task<ServerEntry> AddOrUpdateAsync(Uri baseUri, string name, string? serverId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the saved entry with <paramref name="id"/> through <paramref name="change"/>, read and written in one step,
    /// so a change made meanwhile by someone else is not lost. Its id and address stay; nothing happens when it is no
    /// longer saved. <paramref name="change"/> must not call back into the registry.
    /// </summary>
    Task UpdateAsync(string id, Func<ServerEntry, ServerEntry> change, CancellationToken cancellationToken = default);

    /// <summary>Makes the entry active and bumps its <see cref="ServerEntry.LastUsedAt"/>.</summary>
    Task SetActiveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Removes the entry. If it was active, the most recently used remaining entry becomes active.</summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);
}
