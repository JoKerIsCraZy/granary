using System.Runtime.CompilerServices;

namespace Granary.Tests.Fakes;

/// <summary>Whether something a test let go of is gone from memory, for tests that nothing long-lived keeps a page.</summary>
internal static class Collected
{
    private const int Turns = 20;

    /// <summary>
    /// Whether <paramref name="reference"/>'s object is gone. A test that awaited a view model's task can go on inside the
    /// method that task belongs to, on its thread: that method holds the view model, and with it what listens to it, until
    /// it returns, and a collection from within it finds the object still there (as on a build server, where the task took
    /// a moment). So this steps off that thread and looks again, a few times: an object really kept is there every time.
    /// </summary>
    public static async Task<bool> IsGoneAsync<T>(WeakReference<T> reference)
        where T : class
    {
        for (int turn = 0; turn < Turns; turn++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!IsHeld(reference))
            {
                return true;
            }
            await Task.Yield();
        }
        return false;
    }

    // In a method of its own, so no local of the caller holds the object it looks for.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsHeld<T>(WeakReference<T> reference)
        where T : class => reference.TryGetTarget(out _);
}
