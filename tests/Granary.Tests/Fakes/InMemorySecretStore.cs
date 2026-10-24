using System.Collections.Concurrent;
using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

internal sealed class InMemorySecretStore : ISecretStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
