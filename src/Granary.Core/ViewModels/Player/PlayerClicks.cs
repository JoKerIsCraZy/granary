namespace Granary.Core.ViewModels.Player;

/// <summary>
/// Tells a double click on the player's picture from two clicks, whichever way they reach the page
/// (PlayerPage.Input.cs). WinUI tells a double click only when it saw both clicks. With the cursor hidden the first one
/// goes to the cursor's shield, which shows the cursor and leaves, and WinUI reports the second as a click of its own:
/// a double click would then pause where it should go full screen.
/// </summary>
public sealed class PlayerClicks(TimeProvider time)
{
    private Click? _first;

    // A pair this class told: not acted on yet, or acted on at a time, after which WinUI may still report the same pair.
    private bool _isPairTold;
    private long? _pairTakenAt;

    /// <summary>
    /// A click on the picture at <paramref name="x"/>, <paramref name="y"/> on the screen. True when it is the second of a
    /// double click: no later than <paramref name="within"/> after the one before, and no further than
    /// <paramref name="reach"/> pixels from it. A click after a pair is a first one again.
    /// </summary>
    public bool IsSecond(double x, double y, TimeSpan within, double reach)
    {
        long now = time.GetTimestamp();
        if (_first is { } first && time.GetElapsedTime(first.At, now) <= within
            && Math.Abs(x - first.X) <= reach && Math.Abs(y - first.Y) <= reach)
        {
            _first = null;
            _isPairTold = true;
            _pairTakenAt = null;
            return true;
        }
        // A new first click: no report of an earlier pair is still to come.
        _first = new Click(x, y, now);
        _isPairTold = false;
        _pairTakenAt = null;
        return false;
    }

    /// <summary>
    /// A double click is about to be acted on. False only for WinUI's own report of a pair this class told and that was
    /// acted on within <paramref name="within"/>: that one pair must not act a second time. The next pair acts again,
    /// however soon it follows.
    /// </summary>
    public bool TakesDouble(TimeSpan within)
    {
        long now = time.GetTimestamp();
        _first = null;
        if (_isPairTold)
        {
            _isPairTold = false;
            _pairTakenAt = now;
            return true;
        }
        if (_pairTakenAt is { } at)
        {
            _pairTakenAt = null;
            if (time.GetElapsedTime(at, now) <= within)
            {
                return false;
            }
        }
        return true;
    }

    private readonly record struct Click(double X, double Y, long At);
}
