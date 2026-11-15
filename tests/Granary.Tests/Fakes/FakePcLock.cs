using Granary.Core.Remote;

namespace Granary.Tests.Fakes;

/// <summary>Whether Windows is locked, as the test says; unlocked unless it says otherwise.</summary>
internal sealed class FakePcLock : IPcLock
{
    private bool _isLocked;

    public bool IsLocked
    {
        get => Volatile.Read(ref _isLocked);
        set => Volatile.Write(ref _isLocked, value);
    }
}
