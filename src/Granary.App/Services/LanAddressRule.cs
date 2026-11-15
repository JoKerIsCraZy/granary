using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Granary.Core.Remote;

namespace Granary.App.Services;

/// <summary>One address of one of the PC's adapters, as <see cref="LanAddressRule"/> weighs it.</summary>
/// <param name="Address">The adapter's unicast address.</param>
/// <param name="InterfaceIndex">The adapter's IPv4 index, which the DNS-SD registration names.</param>
/// <param name="IsUp">The adapter is up.</param>
/// <param name="Type">The adapter's kind.</param>
/// <param name="HasIPv4Gateway">The adapter has an IPv4 default gateway.</param>
internal sealed record LanCandidate(IPAddress Address, uint InterfaceIndex, bool IsUp, NetworkInterfaceType Type, bool HasIPv4Gateway)
{
    public override string ToString() => nameof(LanCandidate);
}

/// <summary>
/// Which of the PC's addresses the phone remote listens on and names in its code (design 5.3, the spike's rule): of the
/// adapters that are up and neither loopback nor a tunnel, a private IPv4 address (10/8, 172.16/12, 192.168/16) or a
/// link-local one (169.254/16), those on an adapter with an IPv4 default gateway first, then the first listed. A home
/// network has a gateway; the virtual switches of Hyper-V and WSL usually have none. IPv4 only: the phones' link parsers
/// take an IPv4 literal only.
/// </summary>
internal static class LanAddressRule
{
    public static LanAddress? Choose(IEnumerable<LanCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var usable = candidates.Where(candidate => candidate.IsUp
            && candidate.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            && IsPrivateIPv4(candidate.Address)).ToList();
        var chosen = usable.FirstOrDefault(candidate => candidate.HasIPv4Gateway) ?? usable.FirstOrDefault();
        return chosen is null ? null : new LanAddress(chosen.Address, chosen.InterfaceIndex);
    }

    // The receiver's own test of a local peer, without loopback, for IPv4 itself (not an IPv4-mapped IPv6 address).
    private static bool IsPrivateIPv4(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && RemoteReceiverPolicy.IsLocalPeer(address);
}
