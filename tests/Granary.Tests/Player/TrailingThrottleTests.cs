using Granary.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.Player;

public sealed class TrailingThrottleTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private readonly ManualTimeProvider _time = new();
    private readonly TrailingThrottle _throttle;
    private int _runs;

    public TrailingThrottleTests() => _throttle = new TrailingThrottle(Interval, _time, () => _runs++);

    [Fact]
    public void TheFirstChange_RunsAtOnce()
    {
        _throttle.Signal();

        Assert.Equal(1, _runs);
        Assert.Null(_time.NextDue);
    }

    [Fact]
    public void ChangesWithinTheInterval_RunOnce_WhenItIsOver()
    {
        _throttle.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _throttle.Signal();
        _throttle.Signal();

        _time.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(1, _runs);

        // The last change is never lost: it runs at the end of the interval.
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, _runs);

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, _runs);
    }

    [Fact]
    public void AChangeAfterAQuietInterval_RunsAtOnce()
    {
        _throttle.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(300));

        _throttle.Signal();

        Assert.Equal(2, _runs);
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
        Assert.Equal(4, _runs);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(5, _runs);
    }

    [Fact]
    public void AReset_DropsAWaitingRun_AndTheNextChangeRunsAtOnce()
    {
        _throttle.Signal();
        _throttle.Signal();

        _throttle.Reset();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _runs);

        _throttle.Signal();
        _throttle.Reset();
        _throttle.Signal();
        Assert.Equal(3, _runs);
    }

    [Fact]
    public void AfterDispose_NothingRuns()
    {
        _throttle.Signal();
        _throttle.Signal();

        _throttle.Dispose();
        _time.Advance(TimeSpan.FromSeconds(1));
        _throttle.Signal();

        Assert.Equal(1, _runs);
    }

    [Fact]
    public void TheTimer_IsMadeOnce_AndUsedAgain()
    {
        // No timer per change: the property changes several times a second for as long as the stream loads.
        var counting = new CountingTimeProvider(_time);
        using var throttle = new TrailingThrottle(Interval, counting, () => { });

        for (int i = 0; i < 20; i++)
        {
            throttle.Signal();
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(1, counting.Timers);
    }

    public void Dispose() => _throttle.Dispose();

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
