using Granary.Core.Abstractions;
using Microsoft.UI.Dispatching;

namespace Granary.App.Services;

internal sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public void Post(Action action) => queue.TryEnqueue(() => action());
}
