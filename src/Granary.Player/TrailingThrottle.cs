namespace Granary.Player;

/// <summary>
/// Runs an action for a change at most once per interval: the first change at once, those within the interval once
/// it is over, so the last change always arrives. The action always runs on the timer's thread, never in
/// <see cref="Signal"/>, which only takes a lock; it never runs twice at a time, and not at all once
/// <see cref="Dispose"/> returned. An exception it throws goes to the failure callback, and the throttle goes on.
/// </summary>
internal sealed class TrailingThrottle : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly Action _run;
    private readonly Action<Exception> _failed;
    private readonly Lock _gate = new();

    // Held while the action runs: runs come one after another, and Dispose waits for one under way.
    private readonly Lock _running = new();

    // Made on the first change, and used again for every later one.
    private ITimer? _timer;
    private bool _waiting;
    private long? _lastRun;
    private bool _disposed;

    public TrailingThrottle(TimeSpan interval, TimeProvider time, Action run, Action<Exception> failed)
    {
        _interval = interval;
        _time = time;
        _run = run;
        _failed = failed;
    }

    /// <summary>Something changed: the action runs now on the timer, or once the interval since the last run is over.</summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (_disposed || _waiting)
            {
                return;
            }
            var since = _lastRun is { } last ? _time.GetElapsedTime(last) : _interval;
            _waiting = true;
            _timer ??= _time.CreateTimer(_ => RunWaiting(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(since < _interval ? _interval - since : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Drops a run still waiting; the next change runs at once. For a new start, where nothing before counts.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _waiting = false;
            _lastRun = null;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    // On the timer's thread. A run still under way is waited for; a Reset or Dispose meanwhile drops this one.
    private void RunWaiting()
    {
        lock (_running)
        {
            lock (_gate)
            {
                if (_disposed || !_waiting)
                {
                    return;
                }
                _waiting = false;
                _lastRun = _time.GetTimestamp();
            }
            try
            {
                _run();
            }
            catch (Exception ex)
            {
                // A timer's thread has nobody to throw to: an exception there would end the app.
                _failed(ex);
            }
        }
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            _disposed = true;
            _waiting = false;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        // A run under way finishes before this returns.
        lock (_running)
        {
        }
    }
}
