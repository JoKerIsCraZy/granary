namespace Granary.Tests.Fakes;

/// <summary>
/// A clock that moves only when the test advances it; timers fire inside <see cref="Advance"/>, in due
/// order. <see cref="WaitForTimerAsync"/> tells the test that the code under test is parked on a timer.
/// (Microsoft.Extensions.TimeProvider.Testing is not referenced.)
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public static readonly DateTimeOffset DefaultStart = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // Only a safety net for a test whose code under test never waits; tests do not wait this long.
    private static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly DateTimeOffset _start = start;
    private TaskCompletionSource _timersChanged = NewSignal();
    private DateTimeOffset _now = start;

    public ManualTimeProvider()
        : this(DefaultStart)
    {
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>How far the clock has moved since it was created.</summary>
    public TimeSpan Elapsed => GetUtcNow() - _start;

    /// <summary>Time until the earliest scheduled timer fires; null when none is scheduled.</summary>
    public TimeSpan? NextDue
    {
        get
        {
            lock (_gate)
            {
                return Earliest() is { Due: { } due } ? due - _now : null;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward by <paramref name="by"/>, firing every timer that falls due on the way.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = Earliest();
                if (next?.Due is not { } due || due > target)
                {
                    _now = target;
                    return;
                }
                if (due > _now)
                {
                    _now = due;
                }
                next.Reschedule(_now);
                Signal();
            }
            next.Fire();
        }
    }

    /// <summary>Completes once at least one timer is scheduled.</summary>
    public Task WaitForTimerAsync(CancellationToken cancellationToken) => WaitUntilAsync(() => NextDue is not null, cancellationToken);

    /// <summary>Completes once <paramref name="condition"/> holds; it is checked again whenever a timer changes.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                changed = _timersChanged.Task;
            }
            if (condition())
            {
                return;
            }
            await changed.WaitAsync(HangTimeout, cancellationToken);
        }
    }

    /// <summary>Waits for the next timer and moves the clock exactly to it. Returns how far the clock moved.</summary>
    public async Task<TimeSpan> FireNextTimerAsync(CancellationToken cancellationToken)
    {
        await WaitForTimerAsync(cancellationToken);
        var due = NextDue ?? TimeSpan.Zero;
        Advance(due);
        return due;
    }

    private ManualTimer? Earliest() => _timers.Where(t => t.Due is not null).MinBy(t => t.Due);

    private void Signal()
    {
        var changed = _timersChanged;
        _timersChanged = NewSignal();
        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return false;
                }
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                _period = period;
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }
                owner.Signal();
                return true;
            }
        }

        /// <summary>Called under the owner's lock when the timer fires at <paramref name="now"/>.</summary>
        public void Reschedule(DateTimeOffset now) =>
            Due = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : now + _period;

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                Due = null;
                owner._timers.Remove(this);
                owner.Signal();
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
