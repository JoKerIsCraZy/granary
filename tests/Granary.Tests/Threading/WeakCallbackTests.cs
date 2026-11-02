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

    [Fact]
    public void ACallbackHoldingItsOwner_IsRefused()
    {
        var owner = new Counter();

        Assert.Throws<ArgumentException>(() => new WeakCallback<Counter>(owner, _ => owner.Count++));
    }

    [Fact]
    public void ACallbackHoldingALocal_IsRefused()
    {
        var owner = new Counter();
        int step = 2;

        Assert.Throws<ArgumentException>(() => new WeakCallback<Counter>(owner, counter => counter.Count += step));
    }

    [Fact]
    public void AnInstanceMethodOfTheOwner_IsRefused()
    {
        var owner = new Counter();

        Assert.Throws<ArgumentException>(() => new WeakCallback<Counter>(owner, owner.Bump));
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

        public void Bump(Counter _) => Count++;
    }
}
