using System.Net;
using System.Net.Sockets;

namespace Granary.Core.Api.Playback;

/// <summary>
/// What a media node's host is, for the checks of <see cref="PlaybackUrl"/>: written forms are taken as what they reach, an
/// IPv6 address carrying an IPv4 one as that IPv4 address.
/// </summary>
internal static class NetworkAddresses
{
    /// <summary>The host as it is reached: without IPv6 brackets or the root's trailing dot, which leave the same host.</summary>
    public static string HostOf(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return Normalize(uri.IdnHost);
    }

    public static string Normalize(string host) => host.Trim('[', ']').TrimEnd('.');

    /// <summary><c>localhost</c> and every name under it are this machine (RFC 6761).</summary>
    public static bool IsLocalhostName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>The host as an address, unwrapped (<see cref="Unwrap"/>); null for a name.</summary>
    public static IPAddress? Parse(string host) => IPAddress.TryParse(host, out var address) ? Unwrap(address) : null;

    /// <summary>
    /// The IPv4 address an IPv6 one stands for: IPv4-mapped (<c>::ffff:a.b.c.d</c>), IPv4-compatible (<c>::a.b.c.d</c>), 6to4
    /// (<c>2002::/16</c>) and NAT64 (<c>64:ff9b::/96</c>); any other address as it is.
    /// </summary>
    public static IPAddress Unwrap(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }
        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return new IPAddress(bytes[2..6]);
        }
        bool nat64 = bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B && bytes[4..12].All(b => b == 0);
        // The unspecified address and loopback (:: and ::1) are IPv6's own, not IPv4-compatible.
        bool compatible = bytes[..12].All(b => b == 0) && !(bytes[12..15].All(b => b == 0) && bytes[15] <= 1);
        return nat64 || compatible ? new IPAddress(bytes[12..16]) : address;
    }

    /// <summary>Never a node, whoever names it: this machine, a link-local, multicast, unspecified or reserved address.</summary>
    public static bool IsNeverANode(IPAddress address)
    {
        address = Unwrap(address);
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 0 || bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254) || bytes[0] >= 224;
        }
        return IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast;
    }

    /// <summary>On a private network: a private (RFC 1918), shared (100.64/10), unique-local or site-local address.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        address = Unwrap(address);
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        }
        return (bytes[0] & 0xFE) == 0xFC || address.IsIPv6SiteLocal;
    }

    /// <summary>A name only a LAN resolves: one label, <c>.local</c>, <c>.lan</c>, <c>.internal</c>, <c>.home.arpa</c>.</summary>
    public static bool IsLanName(string host) =>
        !host.Contains('.', StringComparison.Ordinal)
        || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
}
