using Granary.Core.Playback;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>The media proxy's watch on the server: only one wait on the server that lasts the limit is cut off.</summary>
public sealed class UpstreamWatchTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    [Fact]
    public void AWaitThatLastsTheLimit_IsCutOff()
    {
        var time = new ManualTimeProvider();
        using var watch = new UpstreamWatch(Limit, time, CancellationToken.None);

        watch.Arm();
        time.Advance(Limit - TimeSpan.FromSeconds(1));
        Assert.False(watch.IsCutOff);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(watch.IsCutOff);
        Assert.True(watch.Token.IsCancellationRequested);
    }

    [Fact]
    public void TimeBetweenWaits_DoesNotCount()
    {
        // Between two reads the proxy writes to the player, which a paused player can hold up for good.
        var time = new ManualTimeProvider();
        using var watch = new UpstreamWatch(Limit, time, CancellationToken.None);
        watch.Arm();
        time.Advance(TimeSpan.FromSeconds(50));
        watch.Disarm();
        time.Advance(TimeSpan.FromMinutes(10));

        watch.Arm();
        time.Advance(TimeSpan.FromSeconds(59));

        Assert.False(watch.IsCutOff);
        Assert.False(watch.Token.IsCancellationRequested);
    }

    [Fact]
    public void ACallbackThatComesAfterItsWaitEnded_CutsNothing()
    {
        // The timer fell due just as the server's bytes came in. Its callback, queued by then, runs once that wait has
        // ended and the next one began: the next one is young, and the stream healthy.
        var time = new LateTimeProvider();
        using var watch = new UpstreamWatch(Limit, time, CancellationToken.None);
        watch.Arm();
        time.Advance(Limit);
        watch.Disarm();
        watch.Arm();

        time.RunDueCallbacks();

        Assert.False(watch.IsCutOff);
        Assert.False(watch.Token.IsCancellationRequested);
    }

    [Fact]
    public void ACallbackAfterTheLastWait_CutsNothing()
    {
        var time = new LateTimeProvider();
        using var watch = new UpstreamWatch(Limit, time, CancellationToken.None);
        watch.Arm();
        time.Advance(Limit);
        watch.Disarm();

        time.RunDueCallbacks();

        Assert.False(watch.Token.IsCancellationRequested);
    }

    [Fact]
    public void ARouteThatCloses_IsNoCut()
    {
        using var closing = new CancellationTokenSource();
        using var watch = new UpstreamWatch(Limit, new ManualTimeProvider(), closing.Token);
        watch.Arm();

        closing.Cancel();

        Assert.True(watch.Token.IsCancellationRequested);
        Assert.False(watch.IsCutOff);
    }

    // A clock whose timers fall due in Advance but whose callbacks run only in RunDueCallbacks, as a thread-pool callback
    // queued before its timer was changed runs late. ManualTimeProvider runs them inside Advance and cannot show this.
    private sealed class LateTimeProvider : TimeProvider
    {
        private readonly List<LateTimer> _timers = [];
        private readonly List<LateTimer> _due = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new LateTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _ticks += by.Ticks;
            foreach (var timer in _timers.Where(t => t.Due is { } due && due <= _ticks))
            {
                timer.Due = null;
                _due.Add(timer);
            }
        }

        public void RunDueCallbacks()
        {
            var due = _due.ToList();
            _due.Clear();
            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class LateTimer(LateTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public long? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose() => Due = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
