namespace Granary.Player;

/// <summary>
/// Runs an action for a change at most once per interval: the first change at once, those within the interval once
/// it is over, so the last change always arrives. Safe from any thread; the action never runs twice at a time, and
/// not at all once <see cref="Dispose"/> returned. It may run on a timer's thread.
/// </summary>
internal sealed class TrailingThrottle : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly Action _run;
    private readonly Lock _gate = new();

    // Held while the action runs: runs come one after another, and Dispose waits for one under way.
    private readonly Lock _running = new();

    // Made on the first change that has to wait, and used again for every later one.
    private ITimer? _timer;
    private bool _waiting;
    private long? _lastRun;
    private bool _disposed;

    public TrailingThrottle(TimeSpan interval, TimeProvider time, Action run)
    {
        _interval = interval;
        _time = time;
        _run = run;
    }

    /// <summary>Something changed: run now, or once the interval since the last run is over.</summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (_disposed || _waiting)
            {
                return;
            }
            var since = _lastRun is { } last ? _time.GetElapsedTime(last) : _interval;
            if (since < _interval)
            {
                _waiting = true;
                _timer ??= _time.CreateTimer(_ => RunWaiting(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _timer.Change(_interval - since, Timeout.InfiniteTimeSpan);
                return;
            }
            _lastRun = _time.GetTimestamp();
        }
        Run();
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

    private void RunWaiting()
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
        Run();
    }

    private void Run()
    {
        lock (_running)
        {
            if (!Volatile.Read(ref _disposed))
            {
                _run();
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
