namespace Granary.Core.Threading;

/// <summary>
/// A callback on an owner that it does not keep alive, for an event source outside XAML that lives longer than the
/// owner, such as a timer: a handler holding its owner there is a cycle the collector cannot see through. It takes a
/// static lambda; one that holds anything, its owner above all, is refused.
/// </summary>
public sealed class WeakCallback<T> where T : class
{
    private readonly WeakReference<T> _owner;
    private readonly Action<T> _callback;

    public WeakCallback(T owner, Action<T> callback)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(callback);
        if (!HoldsNothing(callback))
        {
            throw new ArgumentException("The callback holds state; give it a static lambda that takes the owner.", nameof(callback));
        }
        _owner = new WeakReference<T>(owner);
        _callback = callback;
    }

    /// <summary>Runs the callback on the owner; false once the owner is gone.</summary>
    public bool TryInvoke()
    {
        if (!_owner.TryGetTarget(out var target))
        {
            return false;
        }
        _callback(target);
        return true;
    }

    // A static method, or a lambda that captures nothing: C# puts those on one cached object of a class it names "<>c".
    // A lambda that uses `this` runs on the owner itself, and one that captures locals on an object holding them.
    private static bool HoldsNothing(Action<T> callback) =>
        callback.GetInvocationList().All(part => part.Target is null || part.Target.GetType().Name == "<>c");
}
