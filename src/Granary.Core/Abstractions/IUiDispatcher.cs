namespace Granary.Core.Abstractions;

/// <summary>Runs work on the UI thread. View models use it for state that arrives from other threads.</summary>
public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    void Post(Action action);
}
