using Granary.Core.Remote;

namespace Granary.Tests.Fakes;

/// <summary>The PC's LAN address as a test sets it.</summary>
internal sealed class FakeLanAddressSource : ILanAddressSource
{
    public LanAddress? Current { get; private set; }

    public event EventHandler? Changed;

    public void Set(LanAddress? address)
    {
        Current = address;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
