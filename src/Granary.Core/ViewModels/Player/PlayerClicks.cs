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
    private long? _doubleAt;

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
            return true;
        }
        _first = new Click(x, y, now);
        return false;
    }

    /// <summary>
    /// A double click is about to be acted on. False when one was within <paramref name="within"/> already: WinUI's own
    /// report of a pair this class told first, which must not act a second time.
    /// </summary>
    public bool TakesDouble(TimeSpan within)
    {
        long now = time.GetTimestamp();
        if (_doubleAt is { } at && time.GetElapsedTime(at, now) <= within)
        {
            return false;
        }
        _doubleAt = now;
        _first = null;
        return true;
    }

    private readonly record struct Click(double X, double Y, long At);
}
