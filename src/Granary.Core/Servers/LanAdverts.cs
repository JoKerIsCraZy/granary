using System.Net;
using System.Net.Sockets;

namespace Granary.Core.Servers;

/// <summary>What the platform resolved for one <c>_silo._tcp</c> instance: none of it is trusted.</summary>
/// <param name="InstanceName">The advertised instance name, a fallback label only.</param>
/// <param name="Addresses">Numeric addresses; an IPv6 literal may carry a <c>%scope</c>.</param>
/// <param name="TextRecords">The TXT strings as Windows hands them, <c>key=value</c>.</param>
public sealed record LanAdvertisement(string InstanceName, IReadOnlyList<string> Addresses, int Port, IReadOnlyList<string> TextRecords);

/// <summary>A DNS-SD browser for <c>_silo._tcp</c> in <c>local.</c>; Windows' DNS-SD in the app.</summary>
public interface ILanServiceBrowser
{
    /// <summary>Starts browsing; <paramref name="events"/> may be called on any thread until <see cref="Stop"/>.</summary>
    void Start(ILanBrowseEvents events);

    void Stop();
}

public interface ILanBrowseEvents
{
    /// <summary><paramref name="key"/> resolved, or its addresses, port or TXT changed.</summary>
    void Resolved(string key, LanAdvertisement advertisement);

    /// <summary><paramref name="key"/> is no longer advertised; it may come back.</summary>
    void Gone(string key);

    /// <summary>Browsing could not start, or stopped on its own.</summary>
    void Failed();
}

/// <summary>Finds nothing: a platform without DNS-SD, and the tests that do not browse.</summary>
public sealed class NoLanServiceBrowser : ILanServiceBrowser
{
    public void Start(ILanBrowseEvents events)
    {
    }

    public void Stop()
    {
    }
}

/// <summary>
/// The rules for an <c>_silo._tcp</c> advert (silo-server <c>docs/architecture/server-discovery.md</c>): the TXT
/// <c>id</c> is a hint that names a deployment, the server is reached by the resolved address (never the <c>.local</c>
/// name), and anything on the network can advertise anything, so every value is checked before it is used.
/// </summary>
public static class LanAdverts
{
    public const string ServiceType = "_silo._tcp";

    public const string TxtServerId = "id";

    /// <summary>Addresses tried per advert, so a host with many interfaces stays bounded.</summary>
    public const int MaxOrigins = 4;

    // A TXT string is at most 255 bytes; a server id is far shorter.
    private const int MaxIdLength = 128;

    // What a found server's label may run to: a DNS label's 63, with room for a branding name.
    private const int MaxNameLength = 80;

    /// <summary>The TXT <c>id</c>, or null when absent or unusable. Every other key is ignored, <c>v</c> included.</summary>
    public static string? ServerId(IEnumerable<string> textRecords)
    {
        ArgumentNullException.ThrowIfNull(textRecords);
        foreach (string record in textRecords)
        {
            int equals = record.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && record.AsSpan(0, equals).Trim().Equals(TxtServerId, StringComparison.OrdinalIgnoreCase))
            {
                return UsableId(record[(equals + 1)..]);
            }
        }
        return null;
    }

    /// <summary>A server id as an advert or an identity answer names it: trimmed, short, printable, or null.</summary>
    public static string? UsableId(string? value)
    {
        string? id = value?.Trim();
        return id is { Length: > 0 and <= MaxIdLength } && id.All(c => c > ' ' && c < 0x7F) ? id : null;
    }

    /// <summary>
    /// <c>http://</c> origins for a resolved advert in the order to try them: IPv4 first, then routable IPv6 in brackets.
    /// Link-local IPv6 is left out because a URL cannot carry its interface; loopback, unspecified and multicast
    /// addresses never name a server on this network.
    /// </summary>
    public static IReadOnlyList<Uri> Origins(IEnumerable<string> addresses, int port)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (port is < 1 or > 65535)
        {
            return [];
        }
        var parsed = addresses
            .Select(Parse)
            .OfType<IPAddress>()
            .Distinct()
            .ToList();
        var ipv4 = parsed.Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsUsableIPv4(a));
        var ipv6 = parsed.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && IsUsableIPv6(a));
        return
        [
            .. ipv4.Select(a => new Uri($"http://{a}:{port}/"))
                .Concat(ipv6.Select(a => new Uri($"http://[{a}]:{port}/")))
                .Take(MaxOrigins),
        ];
    }

    /// <summary>
    /// The local route's form of an advertised origin: HTTPS on the same port, and only on a private IPv4 address,
    /// where the pinned key is the only trust that applies (server-identity.md, rule 1). Null otherwise.
    /// </summary>
    public static Uri? LocalRouteOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return IPAddress.TryParse(origin.Host, out var address) && LocalRouteMonitor.IsPrivateIPv4(address)
            ? new Uri($"https://{address}:{origin.Port}/")
            : null;
    }

    /// <summary>A found server's label: its branding name, else the advertised instance name, else "Silo".</summary>
    public static string DisplayName(string? brandingName, string? instanceName) =>
        Clean(brandingName) ?? Clean(instanceName) ?? "Silo";

    // Control characters dropped and the length capped: the label comes from whoever answered.
    private static string? Clean(string? text)
    {
        if (text is null)
        {
            return null;
        }
        string kept = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        if (kept.Length > MaxNameLength)
        {
            kept = kept[..MaxNameLength].TrimEnd() + "…";
        }
        return kept.Length > 0 ? kept : null;
    }

    private static IPAddress? Parse(string? literal)
    {
        string text = (literal ?? "").Trim().TrimStart('/');
        int scope = text.IndexOf('%', StringComparison.Ordinal);
        if (scope >= 0)
        {
            text = text[..scope];
        }
        return IPAddress.TryParse(text, out var address) ? address : null;
    }

    private static bool IsUsableIPv4(IPAddress address)
    {
        byte first = address.GetAddressBytes()[0];
        return first is not (0 or 127) && first < 224;
    }

    private static bool IsUsableIPv6(IPAddress address) =>
        !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.Equals(IPAddress.IPv6Loopback) && !address.Equals(IPAddress.IPv6Any)
        && !address.IsIPv4MappedToIPv6;
}
