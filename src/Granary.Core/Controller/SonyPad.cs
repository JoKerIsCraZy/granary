namespace Granary.Core.Controller;

/// <summary>
/// A DualShock 4 or DualSense as Windows reports it where it is no gamepad to Windows: a plain list of buttons and axes
/// (<c>Windows.Gaming.Input.RawGameController</c>). Read by position, so Cross is <see cref="PadButtons.A"/>, Circle
/// <see cref="PadButtons.B"/>, Square <see cref="PadButtons.X"/> and Triangle <see cref="PadButtons.Y"/>; Share or Create is
/// View and Options is Menu.
/// </summary>
/// <remarks>
/// The buttons' places are the ones Microsoft's own controller data labels for these pads (Square 0, Cross 1, Circle 2,
/// Triangle 3, L1 4, R1 5, Share 8, Options 9, L3 10, R3 11; L2 and R2 as buttons lie between). The left stick and the
/// right stick's X are the first three axes. The last three are the triggers and the right stick's Y, in an order that
/// depends on how Windows lists them: by usage (L2, R2, the stick) or as the pad sends them (the stick, L2, R2). A
/// trigger nobody touches reads 0 and a stick in the middle a half, so the first reading with both triggers out tells
/// which it is; until then the right stick counts as at rest and a pressed trigger as pulled home. One per pad.
/// </remarks>
public sealed class SonyPad
{
    private const int Vendor = 0x054C;

    // DualShock 4 (both revisions and its wireless adapter), DualSense and DualSense Edge.
    private static readonly int[] Products = [0x05C4, 0x09CC, 0x0BA0, 0x0CE6, 0x0DF2];

    private static readonly PadButtons[] ButtonPlaces =
    [
        PadButtons.X, PadButtons.A, PadButtons.B, PadButtons.Y, PadButtons.LeftShoulder, PadButtons.RightShoulder,
        PadButtons.LeftTrigger, PadButtons.RightTrigger, PadButtons.View, PadButtons.Menu, PadButtons.LeftStick, PadButtons.RightStick,
    ];

    // An axis no higher than the first is a trigger at rest; one no lower than the second is a stick near its middle.
    private const double TriggerAtRest = 0.05;
    private const double StickNearMiddle = 0.2;

    private const int AxisCount = 6;

    private AxisOrder _order = AxisOrder.Unknown;

    private enum AxisOrder
    {
        Unknown,

        /// <summary>L2, R2, the right stick's Y.</summary>
        ByUsage,

        /// <summary>The right stick's Y, L2, R2.</summary>
        ByReport,
    }

    public static bool IsKnown(int vendorId, int productId) => vendorId == Vendor && Products.Contains(productId);

    /// <summary>One reading. <paramref name="dpad"/> is the pad's direction switch as D-pad buttons.</summary>
    public PadReading Read(ReadOnlySpan<bool> buttons, ReadOnlySpan<double> axes, PadButtons dpad)
    {
        var pressed = dpad;
        for (int index = 0; index < Math.Min(buttons.Length, ButtonPlaces.Length); index++)
        {
            if (buttons[index])
            {
                pressed |= ButtonPlaces[index];
            }
        }
        bool leftDown = pressed.HasFlag(PadButtons.LeftTrigger);
        bool rightDown = pressed.HasFlag(PadButtons.RightTrigger);
        if (_order == AxisOrder.Unknown && axes.Length >= AxisCount && !leftDown && !rightDown)
        {
            _order = OrderOf(axes[3], axes[5]);
        }
        // A reading short of the last three axes is read as before the order was known.
        var order = axes.Length >= AxisCount ? _order : AxisOrder.Unknown;
        var (rightY, leftTrigger, rightTrigger) = order switch
        {
            AxisOrder.ByUsage => (Up(axes[5]), axes[3], axes[4]),
            AxisOrder.ByReport => (Up(axes[3]), axes[4], axes[5]),
            _ => (0.0, leftDown ? 1.0 : 0.0, rightDown ? 1.0 : 0.0),
        };
        return new PadReading(
            pressed,
            LeftX: axes.Length > 0 ? Right(axes[0]) : 0,
            LeftY: axes.Length > 1 ? Up(axes[1]) : 0,
            RightX: axes.Length > 2 ? Right(axes[2]) : 0,
            RightY: rightY,
            LeftTrigger: leftTrigger,
            RightTrigger: rightTrigger);
    }

    // With both triggers out, the axis that reads 0 is a trigger and the one near a half the stick.
    private static AxisOrder OrderOf(double fourth, double sixth) =>
        fourth <= TriggerAtRest && sixth >= StickNearMiddle ? AxisOrder.ByUsage
        : sixth <= TriggerAtRest && fourth >= StickNearMiddle ? AxisOrder.ByReport
        : AxisOrder.Unknown;

    // Axes run from 0 to 1: left to right, and top to bottom.
    private static double Right(double axis) => Math.Clamp((2 * axis) - 1, -1, 1);

    private static double Up(double axis) => Math.Clamp(1 - (2 * axis), -1, 1);
}
