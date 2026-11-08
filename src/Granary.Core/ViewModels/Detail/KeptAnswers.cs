namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// Answers kept by key for a while, with one fetch per key on its way at a time that every caller waits for. The fetch runs
/// to its end even when its callers give up, so the next caller finds its answer, unless it takes longer than
/// <paramref name="giveUpAfter"/>. A failed answer is not kept, nor one for a key <paramref name="mayKeep"/> no longer allows.
/// </summary>
/// <param name="mayKeep">Whether an answer that came for the key may still be kept: its profile still acts, for example.</param>
internal sealed class KeptAnswers<TKey, TValue>(TimeProvider time, TimeSpan keepFor, TimeSpan giveUpAfter, Func<TKey, bool>? mayKeep = null)
    where TKey : notnull
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, (DateTimeOffset At, TValue Value)> _kept = [];
    private readonly Dictionary<TKey, Task<TValue>> _pending = [];
    // Counts the clearings: an answer for an earlier one is not kept.
    private int _era;

    public async Task<TValue> GetAsync(TKey key, Func<CancellationToken, Task<TValue>> fetch, CancellationToken cancellationToken)
    {
        TaskCompletionSource<TValue>? mine = null;
        Task<TValue>? pending;
        int era;
        lock (_gate)
        {
            if (_kept.TryGetValue(key, out var kept))
            {
                if (time.GetUtcNow() - kept.At < keepFor)
                {
                    return kept.Value;
                }
                _kept.Remove(key);
            }
            era = _era;
            if (!_pending.TryGetValue(key, out pending))
            {
                mine = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = mine.Task;
                _pending[key] = pending;
            }
        }
        // Outside the lock: an answer that comes at once would otherwise be kept as pending.
        if (mine is not null)
        {
            _ = FillAsync(key, fetch, mine, era);
        }
        return await pending.WaitAsync(cancellationToken);
    }

    /// <summary>Forgets every answer, and keeps none that is on its way now.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _era++;
            _kept.Clear();
            _pending.Clear();
        }
    }

    private async Task FillAsync(TKey key, Func<CancellationToken, Task<TValue>> fetch, TaskCompletionSource<TValue> answer, int era)
    {
        try
        {
            TValue value;
            using (var giveUp = new CancellationTokenSource(giveUpAfter, time))
            {
                value = await fetch(giveUp.Token);
            }
            bool keep = mayKeep?.Invoke(key) ?? true;
            lock (_gate)
            {
                Forget(key, answer.Task);
                if (keep && era == _era)
                {
                    var now = time.GetUtcNow();
                    foreach (var old in _kept.Where(entry => now - entry.Value.At >= keepFor).Select(entry => entry.Key).ToList())
                    {
                        _kept.Remove(old);
                    }
                    _kept[key] = (now, value);
                }
            }
            answer.SetResult(value);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                Forget(key, answer.Task);
            }
            answer.SetException(ex);
            // Observed here: every caller may have given up already.
            _ = answer.Task.Exception;
        }
    }

    // Only this fetch's own entry: after a clearing, another fetch may be on its way for the key.
    private void Forget(TKey key, Task<TValue> fetch)
    {
        if (_pending.TryGetValue(key, out var pending) && pending == fetch)
        {
            _pending.Remove(key);
        }
    }
}
