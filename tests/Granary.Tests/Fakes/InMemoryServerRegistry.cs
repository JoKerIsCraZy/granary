using Granary.Core.Servers;

namespace Granary.Tests.Fakes;

/// <summary>A non-persistent <see cref="IServerRegistry"/> for view model and session tests.</summary>
internal sealed class InMemoryServerRegistry : IServerRegistry
{
    private readonly List<ServerEntry> _servers = [];

    public IReadOnlyList<ServerEntry> Servers => _servers;

    public string? ActiveServerId { get; private set; }

    public ServerEntry? Active => ActiveServerId is null ? null : Find(ActiveServerId);

    public event EventHandler? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ServerEntry? Find(string id) => _servers.FirstOrDefault(s => s.Id == id);

    public Task<ServerEntry> AddOrUpdateAsync(Uri baseUri, string name, string? serverId, CancellationToken cancellationToken = default)
    {
        var normalized = ServerAddress.Normalize(baseUri);
        var existing = _servers.FirstOrDefault(s => s.BaseUri == normalized);
        var now = DateTimeOffset.UtcNow;
        var entry = existing is null
            ? new ServerEntry($"srv{_servers.Count + 1}", normalized, name, serverId, now, now, null)
            : existing with { Name = name, ServerId = serverId ?? existing.ServerId };
        if (existing is not null)
        {
            _servers[_servers.IndexOf(existing)] = entry;
        }
        else
        {
            _servers.Add(entry);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(entry);
    }

    public Task UpdateAsync(ServerEntry entry, CancellationToken cancellationToken = default)
    {
        int index = _servers.FindIndex(s => s.Id == entry.Id);
        if (index >= 0)
        {
            _servers[index] = entry;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return Task.CompletedTask;
    }

    public Task SetActiveAsync(string id, CancellationToken cancellationToken = default)
    {
        ActiveServerId = id;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        _servers.RemoveAll(s => s.Id == id);
        if (ActiveServerId == id)
        {
            ActiveServerId = _servers.OrderByDescending(s => s.LastUsedAt).FirstOrDefault()?.Id;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
