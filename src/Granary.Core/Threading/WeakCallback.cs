namespace Granary.Core.Threading;

/// <summary>
/// A callback on an owner that it does not keep alive, for an event source outside XAML that lives longer than the
/// owner, such as a timer: a handler holding its owner there is a cycle the collector cannot see through. Give it a
/// static lambda; one that captures the owner holds it again.
/// </summary>
public sealed class WeakCallback<T>(T owner, Action<T> callback) where T : class
{
    private readonly WeakReference<T> _owner = new(owner);

    /// <summary>Runs the callback on the owner; false once the owner is gone.</summary>
    public bool TryInvoke()
    {
        if (!_owner.TryGetTarget(out var target))
        {
            return false;
        }
        callback(target);
        return true;
    }
}
