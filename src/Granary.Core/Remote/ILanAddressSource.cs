using System.Net;

namespace Granary.Core.Remote;

/// <summary>
/// The one private IPv4 address of this PC on its local network that the receiver listens on and names in its pairing
/// code. The App reads it from Windows' network adapters.
/// </summary>
public interface ILanAddressSource
{
    /// <summary>The address and its adapter's index, or null when the PC is on no local network.</summary>
    LanAddress? Current { get; }

    /// <summary>Raised when <see cref="Current"/> became another address, or none.</summary>
    event EventHandler? Changed;
}

/// <summary>A LAN address and the index of the adapter that has it, which the DNS-SD registration names.</summary>
public sealed record LanAddress(IPAddress Address, uint InterfaceIndex)
{
    public override string ToString() => nameof(LanAddress);
}

/// <summary>Core's stand-in where the App gives no address source: no local network, so no receiver.</summary>
public sealed class NoLanAddressSource : ILanAddressSource
{
    public LanAddress? Current => null;

    public event EventHandler? Changed
    {
        add
        {
        }
        remove
        {
        }
    }
}
