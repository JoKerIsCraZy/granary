using System.Collections.Concurrent;
using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

internal sealed class InMemorySecretStore : ISecretStore
{
    private int _writes;

    public ConcurrentDictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>What every read waits for first: done at once, unless a test holds reads back to let callers meet.</summary>
    public Task ReadGate { get; set; } = Task.CompletedTask;

    /// <summary>How many values were written.</summary>
    public int Writes => Volatile.Read(ref _writes);

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await ReadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Values.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>What every write throws, if a test makes the store fail.</summary>
    public Exception? FailWrites { get; set; }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (FailWrites is { } failure)
        {
            return Task.FromException(failure);
        }
        Values[key] = value;
        Interlocked.Increment(ref _writes);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
