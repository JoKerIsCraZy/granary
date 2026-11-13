using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Granary.Core.Remote;

/// <summary>
/// What the receiver says about itself, in its DNS-SD record, its pairing link and its <c>hello</c>. Its
/// <see cref="ToString"/> shows none of it: the server id is the server's address.
/// </summary>
/// <param name="ReceiverId">The receiver's own stable id, under which every phone keeps the pairing key.</param>
/// <param name="Name">The name the phones show for this PC.</param>
/// <param name="ServerId">The id of the server Granary is signed in to (<see cref="SiloCastServerId.From"/>).</param>
/// <param name="ServerName">The server's name, which the phones show under the PC's.</param>
/// <param name="ServerIdentity">The deployment's identity, when Granary knows it, so a phone at another address of the server matches.</param>
public sealed record ReceiverIdentity(string ReceiverId, string Name, string ServerId, string ServerName, string? ServerIdentity)
{
    public override string ToString() => nameof(ReceiverIdentity);
}

/// <summary>Where the receiver listens: the one LAN address, the port bound there, and the index of that address's adapter.</summary>
public sealed record ReceiverEndpoint(IPAddress Address, int Port, uint InterfaceIndex);

/// <summary>
/// One DNS-SD instance for Windows to register. Its <see cref="ToString"/> shows none of it: the TXT pairs hold the
/// server's address.
/// </summary>
/// <param name="InstanceName">The instance's own name, without the service type.</param>
/// <param name="ServiceType">The service type, <see cref="ReceiverAdvertisement.ServiceType"/>.</param>
/// <param name="HostName">The host name whose address record names <paramref name="Address"/>.</param>
/// <param name="Address">The one LAN address the receiver listens on.</param>
/// <param name="Port">The port bound there.</param>
/// <param name="InterfaceIndex">The index of the adapter that has <paramref name="Address"/>.</param>
/// <param name="Txt">The TXT pairs, in the order they are written.</param>
public sealed record ServiceRegistration(
    string InstanceName,
    string ServiceType,
    string HostName,
    IPAddress Address,
    int Port,
    uint InterfaceIndex,
    IReadOnlyList<KeyValuePair<string, string>> Txt)
{
    public override string ToString() => nameof(ServiceRegistration);
}

/// <summary>
/// The DNS-SD record and the pairing link of a receiver of the Roku type, as pure functions. The service type and TXT
/// keys are the ones Silo's Roku app advertises (silo-roku's <c>controlTxt</c> in <c>AppSceneControl.bs</c>, <c>mdns.bs</c>)
/// and the phones read (silo-android's <c>SiloCastNsdBrowser.targetFromRecord</c>, silo-apple's
/// <c>SiloControlBrowser.makeTarget</c>); the link is the one they parse (<c>SiloCastPairingLink.parse</c>,
/// <c>SiloControlPairingLink</c>).
/// </summary>
public static class ReceiverAdvertisement
{
    /// <summary>The Roku type's service: the phones dial it over plain TCP, sealed when TXT <c>sec</c> says so, never TLS.</summary>
    public const string ServiceType = "_silocast-roku._tcp";

    /// <summary>The protocol version in TXT <c>v</c> and the link's <c>v</c>.</summary>
    public const int ProtocolVersion = 2;

    /// <summary>The longest TXT pair: one DNS character string.</summary>
    public const int MaxTxtPairBytes = 255;

    private const int ShortIdLength = 8;

    /// <summary>
    /// The registration for <paramref name="endpoint"/>, or null when a TXT pair does not fit one DNS string: the phones
    /// would read a cut value as another server, so Granary then advertises nothing and pairs through the code alone.
    /// </summary>
    public static ServiceRegistration? For(ReceiverIdentity identity, ReceiverEndpoint endpoint, string keyId, bool isPlaying)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(keyId);

        // The Roku app's order. iOS lists nothing without server, and no sealed receiver without kid; Android drops a sec
        // it does not know. Both read v, name, id, serverName, serverIdentity and playing.
        var txt = new List<KeyValuePair<string, string>>
        {
            new("v", ProtocolVersion.ToString(CultureInfo.InvariantCulture)),
            new("name", identity.Name),
            new("id", identity.ReceiverId),
            new("server", identity.ServerId),
            new("serverName", identity.ServerName),
            new("playing", isPlaying ? "1" : "0"),
        };
        if (!string.IsNullOrEmpty(identity.ServerIdentity))
        {
            txt.Add(new("serverIdentity", identity.ServerIdentity));
        }
        txt.Add(new("sec", "sealed"));
        txt.Add(new("kid", keyId));

        if (txt.Any(pair => Encoding.UTF8.GetByteCount(pair.Key) + 1 + Encoding.UTF8.GetByteCount(pair.Value) > MaxTxtPairBytes))
        {
            return null;
        }
        return new ServiceRegistration(
            InstanceName(identity.ReceiverId), ServiceType, HostName(identity.ReceiverId), endpoint.Address, endpoint.Port,
            endpoint.InterfaceIndex, txt);
    }

    /// <summary>
    /// The link in the pairing code: <c>silo://cast?v=2&amp;host=…&amp;port=…&amp;id=…&amp;name=…&amp;server=…&amp;security=sealed&amp;key=…</c>,
    /// the key in base32. Android wants exactly <c>v=2</c>, an IPv4 literal, a port, an id and the key; Apple also a
    /// <c>server</c>. Values are escaped, a <c>+</c> included, which Android would read as a space. The link holds the
    /// key: it is shown in the code and nowhere else.
    /// </summary>
    public static string PairingLink(ReceiverIdentity identity, ReceiverEndpoint endpoint, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("The phones take an IPv4 address only.", nameof(endpoint));
        }
        if (key.Length != SiloCastSealed.KeyBytes)
        {
            throw new ArgumentException("A pairing key is 16 bytes.", nameof(key));
        }
        string version = ProtocolVersion.ToString(CultureInfo.InvariantCulture);
        string port = endpoint.Port.ToString(CultureInfo.InvariantCulture);
        return $"silo://cast?v={version}&host={endpoint.Address}&port={port}"
            + $"&id={Uri.EscapeDataString(identity.ReceiverId)}"
            + $"&name={Uri.EscapeDataString(identity.Name)}"
            + $"&server={Uri.EscapeDataString(identity.ServerId)}"
            + $"&security=sealed&key={Base32.Encode(key)}";
    }

    /// <summary>The first 8 letters and digits of the receiver's id, in lower case, as Silo's Roku app shortens its own (<c>mdns.bs</c> <c>shortId</c>).</summary>
    public static string ShortId(string receiverId)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        string kept = new(receiverId.Where(char.IsAsciiLetterOrDigit).Take(ShortIdLength).Select(char.ToLowerInvariant).ToArray());
        return kept.Length == 0 ? "granary" : kept;
    }

    /// <summary>The instance's name, Granary's own rather than Silo's: the phones show TXT <c>name</c>, not this.</summary>
    public static string InstanceName(string receiverId) => $"Granary-{ShortId(receiverId)}";

    /// <summary>A host name of the receiver's own, so its address record names the one address it listens on.</summary>
    public static string HostName(string receiverId) => $"granary-{ShortId(receiverId)}.local";
}
