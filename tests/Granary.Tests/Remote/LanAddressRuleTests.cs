using System.Net;
using System.Net.NetworkInformation;
using Granary.App.Services;

namespace Granary.Tests.Remote;

/// <summary>
/// Which of the PC's addresses the phone remote listens on and names in its code (design 5.3), from adapters the test
/// describes: no test reads the PC's own.
/// </summary>
public sealed class LanAddressRuleTests
{
    private static LanCandidate Adapter(string address, uint index = 7, bool up = true, NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        bool gateway = true) => new(IPAddress.Parse(address), index, up, type, gateway);

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.20")]
    [InlineData("169.254.10.10")]
    public void APrivateOrLinkLocalIPv4Address_IsTaken(string address) =>
        Assert.Equal(new Granary.Core.Remote.LanAddress(IPAddress.Parse(address), 7), LanAddressRule.Choose([Adapter(address)]));

    [Theory]
    [InlineData("172.32.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::5")]
    [InlineData("::ffff:192.168.1.20")]
    public void AnyOtherAddress_IsNot(string address) => Assert.Null(LanAddressRule.Choose([Adapter(address)]));

    [Fact]
    public void AnAdapterThatIsDown_ALoopbackOrATunnel_IsLeftOut()
    {
        Assert.Null(LanAddressRule.Choose(
        [
            Adapter("192.168.1.20", up: false),
            Adapter("192.168.1.21", type: NetworkInterfaceType.Loopback),
            Adapter("192.168.1.22", type: NetworkInterfaceType.Tunnel),
        ]));
    }

    [Fact]
    public void AnAdapterWithADefaultGateway_ComesFirst_ThenTheFirstListed()
    {
        // A virtual switch without a gateway is listed before the home network.
        var chosen = LanAddressRule.Choose(
        [
            Adapter("172.20.0.1", index: 21, gateway: false),
            Adapter("192.168.1.20", index: 7),
            Adapter("10.0.0.5", index: 9),
        ]);

        Assert.Equal(new Granary.Core.Remote.LanAddress(IPAddress.Parse("192.168.1.20"), 7), chosen);
    }

    [Fact]
    public void WithoutAnyGateway_TheFirstPrivateAddressWins() =>
        Assert.Equal(IPAddress.Parse("172.20.0.1"), LanAddressRule.Choose(
        [
            Adapter("8.8.8.8", gateway: false),
            Adapter("172.20.0.1", index: 21, gateway: false),
            Adapter("192.168.56.1", index: 30, gateway: false),
        ])!.Address);

    [Fact]
    public void NoAdapter_IsNoAddress() => Assert.Null(LanAddressRule.Choose([]));
}
