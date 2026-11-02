using System.Runtime.CompilerServices;
using Granary.Core.Threading;

namespace Granary.Tests.Threading;

public sealed class WeakCallbackTests
{
    [Fact]
    public void TheCallback_RunsOnItsOwner()
    {
        var owner = new Counter();
        var callback = new WeakCallback<Counter>(owner, static counter => counter.Count++);

        Assert.True(callback.TryInvoke());
        Assert.True(callback.TryInvoke());

        Assert.Equal(2, owner.Count);
        GC.KeepAlive(owner);
    }

    [Fact]
    public void TheCallback_DoesNotKeepItsOwnerAlive()
    {
        var (callback, owner) = CallbackForAnOwnerNobodyHolds();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(owner.TryGetTarget(out _));
        Assert.False(callback.TryInvoke());
    }

    // Made in a method of its own, so no local of the test keeps the owner alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakCallback<Counter> Callback, WeakReference<Counter> Owner) CallbackForAnOwnerNobodyHolds()
    {
        var owner = new Counter();
        return (new WeakCallback<Counter>(owner, static counter => counter.Count++), new WeakReference<Counter>(owner));
    }

    private sealed class Counter
    {
        public int Count { get; set; }
    }
}
