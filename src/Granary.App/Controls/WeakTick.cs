using Granary.Core.Threading;
using Microsoft.UI.Dispatching;

namespace Granary.App.Controls;

/// <summary>
/// A timer's Tick that does not keep its owner alive. The timer is not a XAML object, so the collector cannot see that a
/// handler holding its control is a cycle (control, timer, handler, control): a page with such a timer stayed in memory
/// for good once the frame let it go, and every full collection walked it again. Give it a static lambda. A timer whose
/// owner is gone stops.
/// </summary>
internal static class WeakTick
{
    public static void Subscribe<T>(DispatcherQueueTimer timer, T owner, Action<T> tick) where T : class
    {
        var callback = new WeakCallback<T>(owner, tick);
        timer.Tick += (sender, _) =>
        {
            if (!callback.TryInvoke())
            {
                sender.Stop();
            }
        };
    }
}
