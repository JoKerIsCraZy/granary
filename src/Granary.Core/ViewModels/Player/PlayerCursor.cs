namespace Granary.Core.ViewModels.Player;

/// <summary>
/// The mouse cursor over the player's picture (PlayerPage.AutoHide.cs): it follows the pointer only. A real move of the
/// mouse, or any move of a finger or a pen, shows it, and it hides with the controls while it is over the picture,
/// whatever hid them: the idle wait, a remote's Back, a tap.
/// Keys and the player's own changes show the controls and leave the cursor as it is, so a remote never brings it back
/// over the picture. While the controls are hidden, a cursor that was off the picture as they hid (another window over
/// it) hides once it is seen over the picture again.
/// </summary>
public sealed class PlayerCursor(TimeProvider time)
{
    /// <summary>Moves shorter than this, in view pixels, are jitter, or the framework testing a still pointer again.</summary>
    public const double MoveThreshold = 2;

    /// <summary>
    /// Small moves count once they add up to <see cref="MoveThreshold"/> within this time; drift, or a knock on the desk now
    /// and then, never does.
    /// </summary>
    public static readonly TimeSpan MoveWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How often pointer events that are no move may have the cursor looked at again: they come at the mouse's report
    /// rate, and each look asks Windows where the pointer is.
    /// </summary>
    public static readonly TimeSpan LookInterval = TimeSpan.FromMilliseconds(250);

    // Where the pointer was when the moves now adding up started, and where it was seen last.
    private Sample? _from;
    private Sample? _last;
    private bool _areControlsHidden;
    private long? _lookedAt;

    // How long the mouse had lain still before the moves now adding up started.
    private TimeSpan _stillFor;

    public bool IsHidden { get; private set; }

    /// <summary>
    /// The last real move, for the log: how far the mouse went, in screen pixels, from where its moves started to add up,
    /// and how long it had lain still before they started (not the moment between two events of one move).
    /// </summary>
    public (double Distance, TimeSpan SinceSeen) LastMove { get; private set; }

    /// <summary>
    /// How far the mouse went, in screen pixels, since the move that brought the cursor back, that move included. For the
    /// log, where it tells a hand from a knock on the desk: a hand goes on for hundreds of pixels, a knock or the
    /// sensor's noise stops after a few.
    /// </summary>
    public double Travelled { get; private set; }

    /// <summary>The controls are hidden and the cursor was left showing off the picture: it hides once seen over it.</summary>
    public bool WaitsForPicture => _areControlsHidden && !IsHidden;

    /// <summary>
    /// Whether a pointer event that is no move should have the cursor looked at again: only while it waits for the
    /// picture, and at most every <see cref="LookInterval"/>.
    /// </summary>
    public bool IsLookDue => WaitsForPicture && (_lookedAt is not { } at || time.GetElapsedTime(at) >= LookInterval);

    /// <summary>The pointer rests at <paramref name="x"/>, <paramref name="y"/> on the screen as the player opens: no move.</summary>
    public void RestsAt(double x, double y)
    {
        _from = _last = new Sample(x, y, time.GetTimestamp());
        _stillFor = TimeSpan.Zero;
    }

    /// <summary>
    /// The pointer is at <paramref name="x"/>, <paramref name="y"/> on the screen, in pixels, with <paramref name="scale"/>
    /// of them to a view pixel; true for a real move, which shows the cursor (and the page the controls). On the screen,
    /// since the window going full screen or to another display moves the page under a still pointer.
    /// </summary>
    public bool Moves(double x, double y, double scale)
    {
        var seen = new Sample(x, y, time.GetTimestamp());
        var last = _last;
        _last = seen;
        if (!IsHidden && last is { } previous)
        {
            Travelled += Math.Sqrt(((x - previous.X) * (x - previous.X)) + ((y - previous.Y) * (y - previous.Y)));
        }
        if (_from is { } from && time.GetElapsedTime(from.At, seen.At) > MoveWindow)
        {
            // Too long ago to add up: from now on, small moves count from where the pointer was seen last.
            var rested = last ?? from;
            _stillFor = time.GetElapsedTime(rested.At, seen.At);
            _from = rested with { At = seen.At };
        }
        // The way travelled, so a diagonal move counts as much as a straight one.
        double threshold = MoveThreshold * scale;
        double squared = _from is { } start ? ((x - start.X) * (x - start.X)) + ((y - start.Y) * (y - start.Y)) : 0;
        if (_from is not null && squared < threshold * threshold)
        {
            return false;
        }
        double distance = Math.Sqrt(squared);
        LastMove = (distance, _stillFor);
        if (IsHidden)
        {
            Travelled = distance;
        }
        _stillFor = TimeSpan.Zero;
        _from = seen;
        Shows();
        return true;
    }

    /// <summary>
    /// A finger or a pen moved over the page: always a real move, as the page measures only the mouse's place on the
    /// screen. It shows the cursor (and the page the controls).
    /// </summary>
    public void TouchOrPenMoves() => Shows();

    /// <summary>
    /// The controls hid; a cursor over the picture goes with them. <paramref name="restsAt"/> is where the pointer is on
    /// the screen now, when Windows says: moves count from there. The page sees the mouse only where its moves reach it
    /// (not over an open menu, a window in front or off the window), so the place it saw it last can be an old one, and
    /// a pointer event without a move would be measured against that and bring the cursor back.
    /// </summary>
    public void ControlsHidden(bool isOverPicture, (double X, double Y)? restsAt = null)
    {
        _areControlsHidden = true;
        IsHidden |= isOverPicture;
        Rests(restsAt);
    }

    /// <summary>The controls show again, after a key or the player's own change: the cursor stays as it is.</summary>
    public void ControlsShown() => _areControlsHidden = false;

    /// <summary>
    /// The cursor is looked at again without a move: the pointer seen still, the window activated or changed. While the
    /// controls are hidden, a cursor over the picture hides; true when it hid now.
    /// </summary>
    public bool LooksAgain(bool isOverPicture, (double X, double Y)? restsAt = null)
    {
        _lookedAt = time.GetTimestamp();
        if (!WaitsForPicture || !isOverPicture)
        {
            return false;
        }
        IsHidden = true;
        Rests(restsAt);
        return true;
    }

    // As the cursor hides: moves count from where the pointer is now.
    private void Rests((double X, double Y)? at)
    {
        if (IsHidden && at is { } place)
        {
            RestsAt(place.X, place.Y);
        }
    }

    /// <summary>
    /// The player is left: the cursor shows for the pages under it, and the next move counts wherever it starts, should the
    /// page load again.
    /// </summary>
    public void Show()
    {
        Shows();
        _from = _last = null;
        _lookedAt = null;
    }

    private void Shows()
    {
        IsHidden = false;
        _areControlsHidden = false;
    }

    private readonly record struct Sample(double X, double Y, long At);
}
