namespace Granary.Core.ViewModels.Player;

/// <summary>What a row of the sleep timer menu does.</summary>
public enum SleepTimerAction
{
    TurnOff,

    /// <summary>Pauses after <see cref="SleepTimerOption.Minutes"/>.</summary>
    Minutes,

    /// <summary>Stops at the end of the episode or movie that plays: the next episode does not start by itself.</summary>
    EndOfTitle,
}

/// <summary>A row of the sleep timer menu, as the web's <c>SleepTimerMenu</c> words it: "Turn off", "30 min", "End of episode".</summary>
/// <param name="IsDefault">
/// The duration the timer starts on (<c>player.sleep_timer_default_minutes</c>): the menu opens on it while the timer is off.
/// </param>
public sealed record SleepTimerOption(SleepTimerAction Action, int Minutes, string Label, bool IsDefault = false);
