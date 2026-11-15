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
/// Which of the PC's addresses the phone remote listens on and names in its code (design 5.3, after the spike's rule): of
/// the adapters that are up and neither loopback nor a tunnel, a private IPv4 address (10/8, 172.16/12, 192.168/16) or a
/// link-local one (169.254/16), in this order, and within each the first listed:
/// <list type="number">
/// <item>on a wired or wireless adapter (Ethernet, Wi-Fi) with an IPv4 default gateway: the home network;</item>
/// <item>on another kind of adapter with a gateway, such as a VPN's (Wintun, dial-up), which may well come first in
/// Windows' list;</item>
/// <item>on a wired or wireless adapter without a gateway (a static address; also the virtual switches of Hyper-V and
/// WSL, which Windows lists as Ethernet), then on any other without one;</item>
/// <item>a link-local address (an adapter that got none from the network) only when nothing else is there.</item>
/// </list>
/// Windows' interface metric would order them better still, but .NET does not read it. IPv4 only: the phones' link
/// parsers take an IPv4 literal only.
/// </summary>
internal static class LanAddressRule
{
    public static LanAddress? Choose(IEnumerable<LanCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var chosen = candidates.Where(candidate => candidate.IsUp
                && candidate.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                && IsPrivateIPv4(candidate.Address))
            .OrderBy(Rank)
            .FirstOrDefault();
        return chosen is null ? null : new LanAddress(chosen.Address, chosen.InterfaceIndex);
    }

    // Lower is better; OrderBy keeps Windows' order within a rank.
    private static int Rank(LanCandidate candidate) =>
        (IsLinkLocal(candidate.Address) ? 4 : 0) + (candidate.HasIPv4Gateway ? 0 : 2) + (IsWiredOrWireless(candidate.Type) ? 0 : 1);

    private static bool IsWiredOrWireless(NetworkInterfaceType type) => type is NetworkInterfaceType.Ethernet
        or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
        or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211;

    private static bool IsLinkLocal(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out int written) && written == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    // The receiver's own test of a local peer, without loopback, for IPv4 itself (not an IPv4-mapped IPv6 address).
    private static bool IsPrivateIPv4(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && RemoteReceiverPolicy.IsLocalPeer(address);
}
