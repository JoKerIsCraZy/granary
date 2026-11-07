namespace Granary.Core.Abstractions;

/// <summary>
/// A Play/Pause button in the preview Windows shows over the app's taskbar button (its thumbnail toolbar), so a title can be
/// paused from the taskbar, the window minimized too. The player shows it and takes its presses as its own Play/Pause.
/// </summary>
public interface ITaskbarPlayButton
{
    /// <summary>The button was pressed; raised on the UI thread, inside the window's message.</summary>
    event EventHandler? Pressed;

    /// <summary>Shows the button as <paramref name="state"/> says, or changes it to that.</summary>
    void Show(TaskbarPlayButtonState state);

    /// <summary>Takes the button out of the preview: nothing plays any more.</summary>
    void Hide();
}

/// <summary>
/// What the button shows: Play while paused, else Pause, and whether a press does anything now (off while a title loads,
/// or for a guest of a Watch Party the room does not let pause).
/// </summary>
public sealed record TaskbarPlayButtonState(bool IsPaused, bool IsEnabled);
