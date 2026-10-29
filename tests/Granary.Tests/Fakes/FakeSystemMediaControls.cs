using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>Windows' media controls as a list of what the player showed; the test presses their buttons.</summary>
internal sealed class FakeSystemMediaControls : ISystemMediaControls
{
    public event EventHandler<SystemMediaRequest>? Requested;

    /// <summary>Every state the player showed, in order.</summary>
    public List<SystemMediaState> Shown { get; } = [];

    public int Clears { get; private set; }

    /// <summary>What shows now; null before anything showed and after a clear.</summary>
    public SystemMediaState? Current { get; private set; }

    public void Show(SystemMediaState state)
    {
        Shown.Add(state);
        Current = state;
    }

    public void Clear()
    {
        Clears++;
        Current = null;
    }

    public void Press(SystemMediaButton button, double positionSeconds = 0) => Requested?.Invoke(this, new SystemMediaRequest(button, positionSeconds));
}

/// <summary>Holds posted work until the test runs it, as a UI thread that is busy elsewhere.</summary>
internal sealed class QueuedDispatcher : Granary.Core.Abstractions.IUiDispatcher
{
    private readonly Queue<Action> _work = new();

    public bool HasThreadAccess => true;

    public int Pending => _work.Count;

    public void Post(Action action) => _work.Enqueue(action);

    public void RunAll()
    {
        while (_work.TryDequeue(out var action))
        {
            action();
        }
    }
}
