namespace Granary.Core.Playback;

/// <summary>
/// The stops of server playbacks still on their way. Leaving the player does not wait for them, so the page goes at
/// once; closing the app waits for them a moment, so the server keeps where the viewer left off.
/// </summary>
public sealed class PlaybackStops
{
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _pending = [];

    /// <summary>Raised when a tracked stop ended, however it ended, on the thread that ended it.</summary>
    public event EventHandler? Stopped;

    public void Track(Task stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        bool ended;
        lock (_gate)
        {
            ended = stop.IsCompleted;
            if (!ended)
            {
                _pending.Add(stop);
            }
        }
        if (ended)
        {
            _ = stop.Exception;
            Stopped?.Invoke(this, EventArgs.Empty);
            return;
        }
        _ = stop.ContinueWith(
            done =>
            {
                // Whoever made the stop logged its failure; it must not come back as an unobserved exception.
                _ = done.Exception;
                lock (_gate)
                {
                    _pending.Remove(done);
                }
                Stopped?.Invoke(this, EventArgs.Empty);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Waits until every tracked stop is done, or <paramref name="budget"/> has passed; true when they all are.</summary>
    public async Task<bool> WhenStoppedAsync(TimeSpan budget, TimeProvider? time = null)
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _pending];
        }
        if (pending.Length == 0)
        {
            return true;
        }
        // However each one ends: a failed stop counts as done (Track observed its failure).
        var all = Task.WhenAll(pending.Select(stop => stop.ContinueWith(
            static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)));
        var first = await Task.WhenAny(all, Task.Delay(budget, time ?? TimeProvider.System)).ConfigureAwait(false);
        return first == all;
    }
}
