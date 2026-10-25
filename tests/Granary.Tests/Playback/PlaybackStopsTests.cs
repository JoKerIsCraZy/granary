using Granary.Core.Playback;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>Closing the app waits a moment for the stops still on their way.</summary>
public sealed class PlaybackStopsTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    private readonly PlaybackStops _stops = new();
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public async Task WithoutStops_ThereIsNothingToWaitFor()
    {
        var waiting = _stops.WhenStoppedAsync(Budget, _time);

        Assert.True(waiting.IsCompleted);
        Assert.True(await waiting);
    }

    [Fact]
    public async Task AStopOnItsWay_IsWaitedFor()
    {
        var stop = new TaskCompletionSource();
        _stops.Track(stop.Task);

        var waiting = _stops.WhenStoppedAsync(Budget, _time);
        Assert.False(waiting.IsCompleted);
        stop.SetResult();

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStopThatTakesTooLong_IsLeftBehind()
    {
        _stops.Track(new TaskCompletionSource().Task);

        var waiting = _stops.WhenStoppedAsync(Budget, _time);
        await _time.WaitForTimerAsync(TestContext.Current.CancellationToken);
        _time.Advance(Budget);

        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailedStop_CountsAsDone()
    {
        var stop = new TaskCompletionSource();
        _stops.Track(stop.Task);
        stop.SetException(new HttpRequestException("connection refused"));

        Assert.True(await _stops.WhenStoppedAsync(Budget, _time).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
