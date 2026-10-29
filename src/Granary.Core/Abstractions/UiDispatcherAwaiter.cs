using System.Runtime.CompilerServices;

namespace Granary.Core.Abstractions;

/// <summary>Awaiting the UI thread: <c>await dispatcher.ResumeOnUi();</c> goes on there, at once when already on it.</summary>
public static class UiDispatcherAwaiting
{
    /// <summary>
    /// Goes on on the UI thread. State that lives there is changed after it, whatever thread an answer completed on:
    /// <c>await</c> alone comes back only where a SynchronizationContext brings it.
    /// </summary>
    public static UiDispatcherAwaiter ResumeOnUi(this IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return new UiDispatcherAwaiter(dispatcher);
    }
}

/// <summary>The awaiter of <see cref="UiDispatcherAwaiting.ResumeOnUi"/>.</summary>
public sealed class UiDispatcherAwaiter(IUiDispatcher dispatcher) : INotifyCompletion
{
    public bool IsCompleted => dispatcher.HasThreadAccess;

    public UiDispatcherAwaiter GetAwaiter() => this;

    public void OnCompleted(Action continuation) => dispatcher.Post(continuation);

    public void GetResult()
    {
    }
}
