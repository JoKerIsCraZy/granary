using Granary.Core.Downloads;

namespace Granary.Tests.Fakes;

/// <summary>
/// A drive that answers <paramref name="free"/> only once the test lets it, as one waking up does, or a share that does not
/// answer; the test's end lets a read still waiting go.
/// </summary>
internal sealed class HeldDiskSpace(long free) : IDiskSpace, IDisposable
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _released.TrySetResult();

    public long? FreeBytes(string folder)
    {
        _released.Task.GetAwaiter().GetResult();
        return free;
    }

    public void Dispose() => Release();
}
