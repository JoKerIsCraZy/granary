using System.Net;
using System.Text;
using Granary.Core.Remote;

namespace Granary.Tests.Remote;

/// <summary>
/// What Granary says about itself on the network: the DNS-SD record both phones list a receiver of the Roku type by
/// (silo-roku's <c>controlTxt</c>, silo-android's <c>SiloCastNsdBrowser</c>, silo-apple's <c>SiloControlBrowser</c>), and
/// the link in the pairing code, as the phones parse it.
/// </summary>
public sealed class ReceiverAdvertisementTests
{
    private const string ReceiverId = "1A2B3C4D-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
    private static readonly string ServerId = SiloCastServerId.From(new Uri("https://silo.example"));
    private static readonly ReceiverEndpoint Endpoint = new(IPAddress.Parse("192.168.1.20"), 52817, 7);

    private static ReceiverIdentity Identity(string? serverIdentity = "deploy-1", string name = "Elena's PC", string? serverId = null) =>
        new(ReceiverId, name, serverId ?? ServerId, "Example Server", serverIdentity);

    [Fact]
    public void TheTxtRecord_HasWhatBothPhonesListAReceiverBy()
    {
        var registration = ReceiverAdvertisement.For(Identity(), Endpoint, SealedVectors.KeyId, isPlaying: true);

        Assert.NotNull(registration);
        Assert.Equal("_silocast-roku._tcp", registration.ServiceType);
        Assert.Equal(Endpoint.Address, registration.Address);
        Assert.Equal(52817, registration.Port);
        Assert.Equal(7u, registration.InterfaceIndex);
        // In the order Silo's Roku app writes them, keys spelled exactly as the phones read them.
        KeyValuePair<string, string>[] expected =
        [
            new("v", "2"),
            new("name", "Elena's PC"),
            new("id", ReceiverId),
            new("server", ServerId),
            new("serverName", "Example Server"),
            new("playing", "1"),
            new("serverIdentity", "deploy-1"),
            new("sec", "sealed"),
            new("kid", SealedVectors.KeyId),
        ];
        Assert.Equal(expected, registration.Txt);
    }

    [Fact]
    public void AnUnknownIdentity_IsLeftOut_AndIdleIsZero()
    {
        var registration = ReceiverAdvertisement.For(Identity(serverIdentity: null), Endpoint, SealedVectors.KeyId, isPlaying: false);

        Assert.NotNull(registration);
        Assert.DoesNotContain(registration.Txt, pair => pair.Key == "serverIdentity");
        Assert.Contains(new KeyValuePair<string, string>("playing", "0"), registration.Txt);
    }

    [Fact]
    public void ATxtPairThatDoesNotFitOneDnsString_MeansNoAdvertisement()
    {
        string longAddress = "https://silo.example/" + new string('a', 300);
        var longServer = Identity(serverId: SiloCastServerId.From(new Uri(longAddress)));

        Assert.Null(ReceiverAdvertisement.For(longServer, Endpoint, SealedVectors.KeyId, isPlaying: false));
        Assert.Null(ReceiverAdvertisement.For(Identity(name: new string('é', 130)), Endpoint, SealedVectors.KeyId, isPlaying: false));
        // 255 bytes exactly still fit: "name=" and 250 letters.
        var fits = ReceiverAdvertisement.For(Identity(name: new string('e', 250)), Endpoint, SealedVectors.KeyId, isPlaying: false);
        Assert.NotNull(fits);
        Assert.All(fits.Txt, pair => Assert.InRange(Encoding.UTF8.GetByteCount($"{pair.Key}={pair.Value}"), 1, 255));
    }

    [Fact]
    public void TheInstanceAndHost_AreNamedAfterTheReceiversId()
    {
        var registration = ReceiverAdvertisement.For(Identity(), Endpoint, SealedVectors.KeyId, isPlaying: false);

        Assert.NotNull(registration);
        Assert.Equal("Granary-1a2b3c4d", registration.InstanceName);
        Assert.Equal("granary-1a2b3c4d.local", registration.HostName);
        Assert.Equal("1a2b3c4d", ReceiverAdvertisement.ShortId(ReceiverId));
        Assert.Equal("ab12cd34", ReceiverAdvertisement.ShortId("-AB-12_cd.34ef"));
    }

    [Fact]
    public void TheLink_IsTheOneSilosAndroidAppParses()
    {
        string link = ReceiverAdvertisement.PairingLink(Identity(), Endpoint, SealedVectors.Key);

        // The shape of the link silo-android's SiloCastRokuTransportTest.readsThePairingLink parses.
        Assert.Equal(
            $"silo://cast?v=2&host=192.168.1.20&port=52817&id={ReceiverId}&name=Elena%27s%20PC&server={ServerId}"
            + $"&security=sealed&key={SealedVectors.KeyInBase32}",
            link);
        var parsed = AndroidPairingLink.Parse(link);
        Assert.NotNull(parsed);
        Assert.Equal("192.168.1.20", parsed.Host);
        Assert.Equal(52817, parsed.Port);
        Assert.Equal(ReceiverId, parsed.Id);
        Assert.Equal("Elena's PC", parsed.Name);
        Assert.Equal(ServerId, parsed.Server);
        Assert.Equal(SealedVectors.Key, parsed.Key);
    }

    [Fact]
    public void TheLinksName_IsEscaped()
    {
        string link = ReceiverAdvertisement.PairingLink(Identity(name: "Elena & Zoë #1 + PC"), Endpoint, SealedVectors.Key);

        Assert.Contains("&name=Elena%20%26%20Zo%C3%AB%20%231%20%2B%20PC&", link, StringComparison.Ordinal);
        Assert.Equal("Elena & Zoë #1 + PC", AndroidPairingLink.Parse(link)?.Name);
    }

    /// <summary>
    /// The pairing link as silo-android's <c>SiloCastPairingLink.parse</c> reads it: <c>v=2</c>, an IPv4 literal, a port, an
    /// id and at <c>security=sealed</c> a 16-byte key in base32; values URL-decoded as <c>URLDecoder</c> decodes them, a
    /// <c>+</c> being a space.
    /// </summary>
    private sealed record AndroidPairingLink(string Host, int Port, string Id, string Name, string? Server, byte[] Key)
    {
        public static AndroidPairingLink? Parse(string text)
        {
            const string prefix = "silo://cast?";
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var query = text[prefix.Length..].Split('#')[0].Split('&')
                .Where(part => part.Contains('=', StringComparison.Ordinal))
                .ToDictionary(part => Decode(part[..part.IndexOf('=', StringComparison.Ordinal)]),
                    part => Decode(part[(part.IndexOf('=', StringComparison.Ordinal) + 1)..]));
            string? Value(string key) => query.TryGetValue(key, out var value) && value.Trim().Length > 0 ? value.Trim() : null;

            if (Value("v") != "2" || Value("host") is not { } host || !IsIpv4Literal(host)
                || !int.TryParse(Value("port"), out int port) || port is < 1 or > 65535 || Value("id") is not { } id
                || Value("security") != "sealed" || Value("key") is not { } keyText || Base32.Decode(keyText) is not { Length: 16 } key)
            {
                return null;
            }
            return new AndroidPairingLink(host, port, id, Value("name") ?? "", Value("server"), key);
        }

        private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));

        private static bool IsIpv4Literal(string host)
        {
            string[] parts = host.Split('.');
            return parts.Length == 4 && parts.All(part => part.Length is > 0 and <= 3 && part.All(char.IsAsciiDigit) && int.Parse(part) <= 255);
        }
    }
}
