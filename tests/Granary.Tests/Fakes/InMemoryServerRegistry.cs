using Granary.Core.Servers;

namespace Granary.Tests.Fakes;

/// <summary>A non-persistent <see cref="IServerRegistry"/> for view model and session tests.</summary>
internal sealed class InMemoryServerRegistry : IServerRegistry
{
    private readonly List<ServerEntry> _servers = [];
    private WriteHold? _hold;

    public IReadOnlyList<ServerEntry> Servers => _servers;

    public string? ActiveServerId { get; private set; }

    public ServerEntry? Active => ActiveServerId is null ? null : Find(ActiveServerId);

    public event EventHandler? Changed;

    /// <summary>When set, a change by id throws it, as a save that fails would.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Saves <paramref name="entry"/> as it is, in place of one with the same id.</summary>
    public void Add(ServerEntry entry)
    {
        _servers.RemoveAll(s => s.Id == entry.Id);
        _servers.Add(entry);
    }

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

    public async Task UpdateAsync(string id, Func<ServerEntry, ServerEntry> change, CancellationToken cancellationToken = default)
    {
        await WaitIfHeldAsync();
        if (FailWith is { } failure)
        {
            throw failure;
        }
        int index = _servers.FindIndex(s => s.Id == id);
        if (index >= 0)
        {
            var current = _servers[index];
            var updated = change(current) with { Id = current.Id, BaseUri = current.BaseUri };
            if (updated != current)
            {
                _servers[index] = updated;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Parks the next update until it is released, as a write waiting for the registry's lock would. What it writes is
    /// worked out when it runs.
    /// </summary>
    public WriteHold HoldNextUpdate() => _hold = new WriteHold();

    private async Task WaitIfHeldAsync()
    {
        if (Interlocked.Exchange(ref _hold, null) is { } hold)
        {
            hold.Reached.TrySetResult();
            await hold.Released.Task;
        }
    }

    /// <summary>An update parked by <see cref="HoldNextUpdate"/>.</summary>
    internal sealed class WriteHold
    {
        /// <summary>Completes once the update is parked.</summary>
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => Released.TrySetResult();
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
