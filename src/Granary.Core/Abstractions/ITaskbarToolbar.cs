namespace Granary.Core.Abstractions;

/// <summary>
/// The buttons in the preview Windows shows over the app's taskbar button (its thumbnail toolbar): Previous episode,
/// Play/Pause and Next episode, so a title can be paused or an episode changed from the taskbar, the window minimized too.
/// The player shows them and takes their presses as its own buttons'.
/// </summary>
public interface ITaskbarToolbar
{
    /// <summary>A button was pressed; raised on the UI thread, inside the window's message.</summary>
    event EventHandler<TaskbarButton>? Pressed;

    /// <summary>Shows the buttons as <paramref name="state"/> says, or changes them to that.</summary>
    void Show(TaskbarToolbarState state);

    /// <summary>Takes all the buttons out of the preview: nothing plays any more.</summary>
    void Hide();
}

/// <summary>The toolbar's buttons, in their order there.</summary>
public enum TaskbarButton
{
    Previous,
    PlayPause,
    Next,
}

/// <summary>How a button shows: not at all, greyed (a press does nothing), or working.</summary>
public enum TaskbarButtonState
{
    Hidden,
    Disabled,
    Enabled,
}

/// <summary>
/// What the toolbar shows: each button, and on Play/Pause Play while paused, else Pause. Previous and Next show only for a
/// series episode.
/// </summary>
public sealed record TaskbarToolbarState(TaskbarButtonState Previous, TaskbarButtonState PlayPause, TaskbarButtonState Next, bool IsPaused);
