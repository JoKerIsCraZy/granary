namespace Granary.Core.Controller;

/// <summary>Sticks and triggers as rest, directions and presses (<see cref="PadTuning"/>).</summary>
internal static class PadSticks
{
    /// <summary>The stick as it reads, or at rest inside the dead zone's circle.</summary>
    public static (double X, double Y) PastDeadZone(double x, double y, double radius) =>
        (x * x) + (y * y) <= radius * radius ? (0, 0) : (Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1));

    /// <summary>How far past the dead zone a stick is pushed, from 0 at its edge to 1 all the way, the same way it points.</summary>
    public static (double X, double Y) Push(double x, double y, double radius)
    {
        double length = Math.Sqrt((x * x) + (y * y));
        if (length <= radius)
        {
            return (0, 0);
        }
        double push = Math.Min(1, (length - radius) / (1 - radius));
        return (x / length * push, y / length * push);
    }

    /// <summary>
    /// The stick as a D-pad: the way of its stronger axis from <see cref="PadTuning.StickPress"/> on, and the way it was
    /// going for as long as it stays past <see cref="PadTuning.StickRelease"/>.
    /// </summary>
    public static PadDirection Direction(double x, double y, PadDirection current)
    {
        double along = current switch
        {
            PadDirection.Up => y,
            PadDirection.Down => -y,
            PadDirection.Left => -x,
            PadDirection.Right => x,
            _ => 0,
        };
        if (current != PadDirection.None && along >= PadTuning.StickRelease)
        {
            return current;
        }
        double sideways = Math.Abs(x);
        double upright = Math.Abs(y);
        if (Math.Max(sideways, upright) < PadTuning.StickPress)
        {
            return PadDirection.None;
        }
        return sideways >= upright
            ? (x > 0 ? PadDirection.Right : PadDirection.Left)
            : (y > 0 ? PadDirection.Up : PadDirection.Down);
    }

    /// <summary>A trigger as a button: pressed from half way, and until it is nearly let go.</summary>
    public static bool IsPressed(double pressure, bool wasPressed) =>
        wasPressed ? pressure > PadTuning.TriggerRelease : pressure >= PadTuning.TriggerPress;
}
