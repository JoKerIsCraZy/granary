using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>The taskbar preview's Play/Pause as a list of what the player showed; the test presses it.</summary>
internal sealed class FakeTaskbarPlayButton : ITaskbarPlayButton
{
    public event EventHandler? Pressed;

    /// <summary>Every state the player showed, in order.</summary>
    public List<TaskbarPlayButtonState> Shown { get; } = [];

    public int Hides { get; private set; }

    /// <summary>What shows now; null before anything showed and after a hide.</summary>
    public TaskbarPlayButtonState? Current { get; private set; }

    public void Show(TaskbarPlayButtonState state)
    {
        Shown.Add(state);
        Current = state;
    }

    public void Hide()
    {
        Hides++;
        Current = null;
    }

    public void Press() => Pressed?.Invoke(this, EventArgs.Empty);
}
