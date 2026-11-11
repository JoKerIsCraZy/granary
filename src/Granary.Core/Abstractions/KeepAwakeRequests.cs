namespace Granary.Core.Abstractions;

/// <summary>Windows' kinds of power request (<c>POWER_REQUEST_TYPE</c>, winbase.h), with its numbers.</summary>
public enum PowerRequestType
{
    DisplayRequired = 0,
    SystemRequired = 1,
    AwayModeRequired = 2,
    ExecutionRequired = 3,
}

/// <summary>One kind of power request under one reason, as <c>powercfg /requests</c> lists it for Granary.</summary>
public readonly record struct PowerRequest(string Reason, PowerRequestType Type);

/// <summary>
/// The power requests (<c>PowerCreateRequest</c>, <c>PowerSetRequest</c>) the App's <see cref="IKeepAwake"/> holds for a
/// <see cref="KeepAwakeState"/>: one request object per reason, made once and reused.
/// </summary>
public static class KeepAwakeRequests
{
    public const string VideoReason = "Playing video";
    public const string AudioReason = "Playing audio";

    /// <summary>The reasons, one request object each.</summary>
    public static IReadOnlyList<string> Reasons { get; } = [VideoReason, AudioReason];

    /// <summary>
    /// What is held for <paramref name="state"/>: for a picture the display, and the system with it, as Windows wants both
    /// for a display that stays on; for sound the process, so it is not suspended, and the system, so it does not idle to
    /// sleep; nothing for none.
    /// </summary>
    public static IReadOnlyList<PowerRequest> For(KeepAwakeState state) => state switch
    {
        KeepAwakeState.Display =>
            [new(VideoReason, PowerRequestType.DisplayRequired), new(VideoReason, PowerRequestType.SystemRequired)],
        KeepAwakeState.System =>
            [new(AudioReason, PowerRequestType.ExecutionRequired), new(AudioReason, PowerRequestType.SystemRequired)],
        _ => [],
    };

    /// <summary>
    /// What to set, and then what to clear, to go from the requests <paramref name="active"/> to <paramref name="state"/>:
    /// the new ones are set first, so nothing lapses in between. Only what is missing or left over changes, so a pass after
    /// one that stopped picks up where it stopped, and no request is counted twice.
    /// </summary>
    public static (IReadOnlyList<PowerRequest> Set, IReadOnlyList<PowerRequest> Clear) Changes(
        IReadOnlyCollection<PowerRequest> active, KeepAwakeState state)
    {
        var wanted = For(state);
        return ([.. wanted.Where(request => !active.Contains(request))], [.. active.Where(request => !wanted.Contains(request))]);
    }
}
