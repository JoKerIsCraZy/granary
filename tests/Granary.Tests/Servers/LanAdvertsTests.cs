using Granary.Core.Servers;

namespace Granary.Tests.Servers;

/// <summary>Reading a <c>_silo._tcp</c> advert: nothing in it is trusted, so each part is checked.</summary>
public sealed class LanAdvertsTests
{
    [Theory]
    [InlineData(new[] { "v=1", "id=7d1c2a4e-0f3b" }, "7d1c2a4e-0f3b")]
    [InlineData(new[] { "ID= srv-1 " }, "srv-1")]
    [InlineData(new[] { "v=1" }, null)]
    [InlineData(new[] { "id=" }, null)]
    [InlineData(new[] { "id=   " }, null)]
    [InlineData(new[] { "id" }, null)]
    [InlineData(new[] { "id=a b" }, null)]
    [InlineData(new[] { "id=srv\u0007" }, null)]
    [InlineData(new[] { "idx=srv-1" }, null)]
    public void TheTxtId_IsTheOnlyKeyRead_AndMustBeUsable(string[] records, string? expected) =>
        Assert.Equal(expected, LanAdverts.ServerId(records));

    [Fact]
    public void AnOverlongId_IsRefused() =>
        Assert.Null(LanAdverts.ServerId(["id=" + new string('a', 129)]));

    [Fact]
    public void Origins_TryIPv4First_ThenRoutableIPv6InBrackets()
    {
        var origins = LanAdverts.Origins(["fd00::5", "fe80::1%21", "192.168.1.6", "/10.0.0.2", "192.168.1.6"], 8090);

        Assert.Equal(["http://192.168.1.6:8090/", "http://10.0.0.2:8090/", "http://[fd00::5]:8090/"], origins.Select(o => o.AbsoluteUri));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.251")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("ff02::fb")]
    [InlineData("fe80::82c3:4d59:7aca:a023%21")]
    [InlineData("::ffff:192.168.1.6")]
    [InlineData("silo.local")]
    [InlineData("")]
    public void AddressesThatNameNoServerHere_AreLeftOut(string address) =>
        Assert.Empty(LanAdverts.Origins([address], 8090));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void AnImpossiblePort_GivesNoOrigin(int port) =>
        Assert.Empty(LanAdverts.Origins(["192.168.1.6"], port));

    [Fact]
    public void Origins_StopAtFour() =>
        Assert.Equal(4, LanAdverts.Origins([.. Enumerable.Range(1, 9).Select(i => $"10.0.0.{i}")], 80).Count);

    [Theory]
    [InlineData("http://192.168.1.6:8090/", "https://192.168.1.6:8090/")]
    [InlineData("http://10.1.2.3:80/", "https://10.1.2.3:80/")]
    [InlineData("http://100.64.0.1:8090/", null)]
    [InlineData("http://8.8.8.8:8090/", null)]
    [InlineData("http://[fd00::5]:8090/", null)]
    public void TheLocalRoute_TakesPrivateIPv4AdvertsOverHttps_OnTheirPort(string origin, string? expected) =>
        Assert.Equal(expected, LanAdverts.LocalRouteOrigin(new Uri(origin))?.AbsoluteUri);

    [Theory]
    [InlineData(" Living room ", "Silo", "Living room")]
    [InlineData("", "Silo (2)", "Silo (2)")]
    [InlineData(null, "  ", "Silo")]
    [InlineData("Ki\u0000ds\n", null, "Kids")]
    public void TheLabel_IsTheBrandingName_ElseTheInstanceName_ElseSilo(string? branding, string? instance, string expected) =>
        Assert.Equal(expected, LanAdverts.DisplayName(branding, instance));

    [Fact]
    public void ALongLabel_IsCut() =>
        Assert.Equal(81, LanAdverts.DisplayName(new string('x', 300), null).Length);
}
