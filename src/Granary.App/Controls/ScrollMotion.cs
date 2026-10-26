using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// Whether the cards' scrollers are moving fast, so a card holds its artwork back meanwhile, as the Android app's feeds
/// do (DeferImagePresentationWhileScrolling): its blurred thumbhash stands in. Decoding and drawing each poster that
/// rushed past took the frames the scroll needed, most on a wide window. A slow scroll shows the artwork as the cards
/// come; a fast one as soon as it slows down or stops. Grids and rows follow the scrollers they sit in. UI thread only.
/// </summary>
internal static class ScrollMotion
{
    // About three rows of posters a second, in view pixels: faster, the cards only pass by.
    private const double FastSpeed = 1500;

    // A view change this long after the last starts a new movement: its speed is not known yet.
    private static readonly TimeSpan NewMovementAfter = TimeSpan.FromMilliseconds(250);

    // Wheel turns come about 60 ms apart; a scroller this long without moving has stopped.
    private static readonly TimeSpan SettleAfter = TimeSpan.FromMilliseconds(150);

    private static readonly ConditionalWeakTable<object, Position> Followed = [];
    private static DispatcherQueueTimer? _settle;
    private static TaskCompletionSource? _fast;
    private static double _speed;

    /// <summary>Done at once unless a scroller moves fast, else once it slows down or stops.</summary>
    public static Task WhenSlowAsync() => _fast?.Task ?? Task.CompletedTask;

    /// <summary>Follows a page's scroller; following one again changes nothing.</summary>
    public static void Follow(ScrollViewer scroller)
    {
        if (!Followed.TryGetValue(scroller, out _))
        {
            var position = new Position(scroller.HorizontalOffset + scroller.VerticalOffset);
            Followed.Add(scroller, position);
            scroller.ViewChanged += (_, _) => Moved(position, scroller.HorizontalOffset + scroller.VerticalOffset);
        }
    }

    /// <summary>Follows a row's scroller; following one again changes nothing.</summary>
    public static void Follow(ScrollView scroller)
    {
        if (!Followed.TryGetValue(scroller, out _))
        {
            var position = new Position(scroller.HorizontalOffset + scroller.VerticalOffset);
            Followed.Add(scroller, position);
            scroller.ViewChanged += (_, _) => Moved(position, scroller.HorizontalOffset + scroller.VerticalOffset);
        }
    }

    private static void Moved(Position position, double offset)
    {
        long now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(position.At, now);
        double distance = Math.Abs(offset - position.Offset);
        position.Offset = offset;
        position.At = now;
        // A smoothed speed: a single frame's step says little, and a jump with nothing before it counts in full.
        double speed = elapsed > TimeSpan.Zero ? distance / elapsed.TotalSeconds : 0;
        _speed = elapsed > NewMovementAfter ? speed : _speed * 0.6 + speed * 0.4;
        if (_speed > FastSpeed)
        {
            _fast ??= new TaskCompletionSource();
        }
        else
        {
            Release();
        }
        Settle().Stop();
        Settle().Start();
    }

    private static DispatcherQueueTimer Settle()
    {
        if (_settle is null)
        {
            _settle = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _settle.Interval = SettleAfter;
            _settle.IsRepeating = false;
            _settle.Tick += (_, _) =>
            {
                _speed = 0;
                Release();
            };
        }
        return _settle;
    }

    private static void Release()
    {
        var fast = _fast;
        _fast = null;
        fast?.TrySetResult();
    }

    private sealed class Position(double offset)
    {
        public double Offset { get; set; } = offset;

        public long At { get; set; } = Stopwatch.GetTimestamp();
    }
}
