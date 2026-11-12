namespace Granary.Core.Controller;

/// <summary>
/// A game controller's buttons, named by where they sit: <see cref="A"/> is the bottom face button (Cross on a
/// PlayStation pad), <see cref="B"/> the right one (Circle), <see cref="X"/> the left one (Square) and <see cref="Y"/>
/// the top one (Triangle). <see cref="View"/> is Share or Create, <see cref="Menu"/> is Options.
/// </summary>
[Flags]
public enum PadButtons
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    X = 1 << 2,
    Y = 1 << 3,
    View = 1 << 4,
    Menu = 1 << 5,
    LeftShoulder = 1 << 6,
    RightShoulder = 1 << 7,
    DPadUp = 1 << 8,
    DPadDown = 1 << 9,
    DPadLeft = 1 << 10,
    DPadRight = 1 << 11,
    LeftStick = 1 << 12,
    RightStick = 1 << 13,

    /// <summary>A trigger a pad reports as pressed, beside or instead of how far it is pulled.</summary>
    LeftTrigger = 1 << 14,
    RightTrigger = 1 << 15,
}

public enum PadDirection
{
    None,
    Up,
    Down,
    Left,
    Right,
}

/// <summary>
/// What a pad reports at one moment. Sticks run from -1 to 1, right and up positive; triggers from 0 to 1. The default
/// is a pad at rest.
/// </summary>
public readonly record struct PadReading(
    PadButtons Buttons,
    double LeftX = 0,
    double LeftY = 0,
    double RightX = 0,
    double RightY = 0,
    double LeftTrigger = 0,
    double RightTrigger = 0)
{
    /// <summary>
    /// Two pads as one: any pad in the room works, and a pad that shows up twice (itself and as the Xbox pad a tool makes
    /// of it) still presses each button once.
    /// </summary>
    public static PadReading Merge(PadReading first, PadReading second) => new(
        first.Buttons | second.Buttons,
        Further(first.LeftX, second.LeftX),
        Further(first.LeftY, second.LeftY),
        Further(first.RightX, second.RightX),
        Further(first.RightY, second.RightY),
        Math.Max(first.LeftTrigger, second.LeftTrigger),
        Math.Max(first.RightTrigger, second.RightTrigger));

    private static double Further(double first, double second) => Math.Abs(second) > Math.Abs(first) ? second : first;
}
