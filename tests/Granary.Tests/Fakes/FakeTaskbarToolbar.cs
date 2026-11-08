using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>The taskbar preview's buttons as a list of what the player showed; the test presses them.</summary>
internal sealed class FakeTaskbarToolbar : ITaskbarToolbar
{
    public event EventHandler<TaskbarButton>? Pressed;

    /// <summary>Every state the player showed, in order.</summary>
    public List<TaskbarToolbarState> Shown { get; } = [];

    public int Hides { get; private set; }

    /// <summary>What shows now; null before anything showed and after a hide.</summary>
    public TaskbarToolbarState? Current { get; private set; }

    public void Show(TaskbarToolbarState state)
    {
        Shown.Add(state);
        Current = state;
    }

    public void Hide()
    {
        Hides++;
        Current = null;
    }

    public void Press(TaskbarButton button) => Pressed?.Invoke(this, button);
}
