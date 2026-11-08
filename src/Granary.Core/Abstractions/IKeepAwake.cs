namespace Granary.Core.Abstractions;

/// <summary>
/// Asks Windows to keep the display, or only the system, awake while something plays, so a film watched without touching
/// anything is not cut off by the display's or the sleep timeout. Each request replaces the one before; the player makes
/// them on the UI thread.
/// </summary>
public interface IKeepAwake
{
    /// <summary>
    /// Holds <paramref name="state"/> until the next call; <see cref="KeepAwakeState.None"/> lets go. False when Windows
    /// refused it: what was held before still is.
    /// </summary>
    bool Set(KeepAwakeState state);
}

/// <summary>What playback keeps from sleeping.</summary>
public enum KeepAwakeState
{
    /// <summary>Nothing: Windows' timeouts run as usual.</summary>
    None,

    /// <summary>The system, while sound plays without a picture: the display may turn off.</summary>
    System,

    /// <summary>The display, and the system with it, while a picture plays.</summary>
    Display,
}
