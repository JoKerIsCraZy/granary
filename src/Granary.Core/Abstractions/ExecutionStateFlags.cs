namespace Granary.Core.Abstractions;

/// <summary>
/// The flags of Windows' <c>SetThreadExecutionState</c> (winbase.h) for a <see cref="KeepAwakeState"/>, which the App's
/// <see cref="IKeepAwake"/> falls back on when Windows makes it no power request (<see cref="KeepAwakeRequests"/>). Each is
/// continuous, so it holds until the next call; the display is asked for with the system, as mpv's own window asks.
/// </summary>
public static class ExecutionStateFlags
{
    public const uint SystemRequired = 0x00000001;
    public const uint DisplayRequired = 0x00000002;
    public const uint Continuous = 0x80000000;

    public static uint For(KeepAwakeState state) => state switch
    {
        KeepAwakeState.Display => Continuous | SystemRequired | DisplayRequired,
        KeepAwakeState.System => Continuous | SystemRequired,
        _ => Continuous,
    };
}
