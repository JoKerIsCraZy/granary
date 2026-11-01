using Granary.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.Player;

public sealed class TrailingThrottleTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    // Only a safety net for a gate that is never reached; the tests do not wait this long.
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(10);

    private readonly ManualTimeProvider _time = new();
    private readonly List<Exception> _failures = [];
    private readonly TrailingThrottle _throttle;
    private Action<int> _onRun = _ => { };
    private int _runs;

    public TrailingThrottleTests() =>
        _throttle = new TrailingThrottle(Interval, _time, () => _onRun(Interlocked.Increment(ref _runs)), ex =>
        {
            lock (_failures)
            {
                _failures.Add(ex);
            }
        });

    private int Runs => Volatile.Read(ref _runs);

    [Fact]
    public void TheFirstChange_RunsAtOnce_OnTheTimer()
    {
        _throttle.Signal();

        // Never on the caller's thread: mpv's event thread signals, and must not wait for the read.
        Assert.Equal(0, Runs);
        Assert.Equal(TimeSpan.Zero, _time.NextDue);
        _time.Advance(TimeSpan.Zero);
        Assert.Equal(1, Runs);
    }

    [Fact]
    public async Task WithTheSystemClock_TheFirstChangeRuns()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var throttle = new TrailingThrottle(Interval, TimeProvider.System, () => ran.TrySetResult(), _ => { });

        throttle.Signal();

        await ran.Task.WaitAsync(Hang, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ChangesWithinTheInterval_RunOnce_WhenItIsOver()
    {
        _throttle.Signal();
        _time.Advance(TimeSpan.Zero);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _throttle.Signal();
        _throttle.Signal();

        _time.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(1, Runs);

        // The last change is never lost: it runs at the end of the interval.
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, Runs);

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Runs);
    }

    [Fact]
    public void AChangeAfterAQuietInterval_RunsAtOnce()
    {
        _throttle.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(300));

        _throttle.Signal();

        Assert.Equal(TimeSpan.Zero, _time.NextDue);
        _time.Advance(TimeSpan.Zero);
        Assert.Equal(2, Runs);
    }

    [Fact]
    public void ASteadyStreamOfChanges_RunsFourTimesASecond()
    {
        // mpv reports its cache with every few packets; the seek bar hears of it at most every 250 ms.
        for (int i = 0; i < 100; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(10));
            _throttle.Signal();
        }

        // At 10 ms, 260, 510 and 760 ms within the second, and one more at 1010 ms for the changes since.
        Assert.Equal(4, Runs);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(5, Runs);
    }

    [Fact]
    public void AReset_DropsAWaitingRun_AndTheNextChangeRunsAtOnce()
    {
        _throttle.Signal();
        _time.Advance(TimeSpan.Zero);
        _throttle.Signal();

        _throttle.Reset();
        _throttle.Signal();

        // At once, though the interval since the last run is not over.
        Assert.Equal(TimeSpan.Zero, _time.NextDue);
        _time.Advance(TimeSpan.Zero);
        Assert.Equal(2, Runs);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, Runs);
    }

    [Fact]
    public void AfterDispose_NothingRuns()
    {
        _throttle.Signal();
        _time.Advance(TimeSpan.Zero);
        _throttle.Signal();

        _throttle.Dispose();
        _time.Advance(TimeSpan.FromSeconds(1));
        _throttle.Signal();
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(1, Runs);
    }

    [Fact]
    public async Task AChangeDuringARun_RunsOnceItIsOver()
    {
        using var gate = new RunGate();
        _onRun = run => gate.HoldIf(run == 1);
        _throttle.Signal();
        var firing = Task.Run(() => _time.Advance(TimeSpan.Zero), TestContext.Current.CancellationToken);
        await gate.Entered.WaitAsync(Hang, TestContext.Current.CancellationToken);

        _throttle.Signal();
        gate.Release();
        await firing.WaitAsync(Hang, TestContext.Current.CancellationToken);

        Assert.Equal(1, Runs);
        _time.Advance(Interval);
        Assert.Equal(2, Runs);
    }

    [Fact]
    public async Task Dispose_WaitsForARunUnderWay_AndNothingRunsAfter()
    {
        using var gate = new RunGate();
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _onRun = run =>
        {
            gate.HoldIf(run == 1);
            order.Enqueue("run ended");
        };
        _throttle.Signal();
        var firing = Task.Run(() => _time.Advance(TimeSpan.Zero), TestContext.Current.CancellationToken);
        await gate.Entered.WaitAsync(Hang, TestContext.Current.CancellationToken);
        // A trailing run waits on the timer: Dispose takes that timer away before it waits for the run.
        _throttle.Signal();
        Assert.NotNull(_time.NextDue);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var disposing = Task.Run(() =>
        {
            started.SetResult();
            _throttle.Dispose();
            order.Enqueue("disposed");
        }, TestContext.Current.CancellationToken);

        // Dispose has begun and is past its timer: all that is left is the wait for the run, which the gate holds.
        await started.Task.WaitAsync(Hang, TestContext.Current.CancellationToken);
        await _time.WaitUntilAsync(() => _time.NextDue is null, TestContext.Current.CancellationToken);
        Assert.False(disposing.IsCompleted);
        gate.Release();
        await disposing.WaitAsync(Hang, TestContext.Current.CancellationToken);
        await firing.WaitAsync(Hang, TestContext.Current.CancellationToken);
        Assert.Equal(["run ended", "disposed"], order);
        _time.Advance(TimeSpan.FromSeconds(1));
        _throttle.Signal();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Runs);
    }

    [Fact]
    public async Task AResetDuringARun_DropsTheChangesMadeDuringIt()
    {
        using var gate = new RunGate();
        _onRun = run => gate.HoldIf(run == 1);
        _throttle.Signal();
        var firing = Task.Run(() => _time.Advance(TimeSpan.Zero), TestContext.Current.CancellationToken);
        await gate.Entered.WaitAsync(Hang, TestContext.Current.CancellationToken);
        _throttle.Signal();

        _throttle.Reset();
        gate.Release();
        await firing.WaitAsync(Hang, TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Runs);
        _throttle.Signal();
        _time.Advance(TimeSpan.Zero);
        Assert.Equal(2, Runs);
    }

    [Fact]
    public void ARunThatThrows_IsReported_AndTheThrottleGoesOn()
    {
        // On a timer's thread an exception would end the app.
        _onRun = run =>
        {
            if (run == 1)
            {
                throw new InvalidOperationException("mpv went away");
            }
        };
        _throttle.Signal();

        _time.Advance(TimeSpan.Zero);

        Assert.IsType<InvalidOperationException>(Assert.Single(_failures));
        _time.Advance(Interval);
        _throttle.Signal();
        _time.Advance(TimeSpan.Zero);
        Assert.Equal(2, Runs);
        Assert.Single(_failures);
    }

    [Fact]
    public void TheTimer_IsMadeOnce_AndUsedAgain()
    {
        // No timer per change: the property changes several times a second for as long as the stream loads.
        var counting = new CountingTimeProvider(_time);
        using var throttle = new TrailingThrottle(Interval, counting, () => { }, _ => { });

        for (int i = 0; i < 20; i++)
        {
            throttle.Signal();
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(1, counting.Timers);
    }

    public void Dispose() => _throttle.Dispose();

    // Holds a run until the test lets it go, and tells the test once the run is held.
    private sealed class RunGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void HoldIf(bool hold)
        {
            if (hold)
            {
                _entered.TrySetResult();
                _release.Wait(Hang);
            }
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }

    private sealed class CountingTimeProvider(TimeProvider inner) : TimeProvider
    {
        public int Timers { get; private set; }

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timers++;
            return inner.CreateTimer(callback, state, dueTime, period);
        }
    }
}
