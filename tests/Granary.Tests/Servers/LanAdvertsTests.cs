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
    public void Origins_TryPrivateIPv4First_ThenUniqueLocalIPv6InBrackets()
    {
        var origins = LanAdverts.Origins(["fd00::5", "fe80::1%21", "192.168.1.6", "/10.0.0.2", "192.168.1.6", "172.16.4.4"], 8090);

        Assert.Equal(["http://192.168.1.6:8090/", "http://10.0.0.2:8090/", "http://172.16.4.4:8090/", "http://[fd00::5]:8090/"],
            origins.Select(o => o.AbsoluteUri));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.7")]
    [InlineData("169.254.10.20")]
    [InlineData("100.64.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.1.1")]
    [InlineData("2001:db8::1")]
    [InlineData("2a02:1234::5")]
    [InlineData("fe00::1")]
    public void AnAdvertPointingOffThisNetwork_GivesNoOrigin(string address) =>
        // Probing it would be this device knocking on an address of the advertiser's choosing.
        Assert.Empty(LanAdverts.Origins([address], 8090));

    [Theory]
    [InlineData("fc00::1")]
    [InlineData("fdff:ffff::9")]
    public void UniqueLocalIPv6_IsThisNetwork(string address) =>
        Assert.Single(LanAdverts.Origins([address], 8090));

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

    [Theory]
    [InlineData("\u202Eexe.mooR gniviL", "exe.mooR gniviL")]
    [InlineData("Living\u200B room\u2066", "Living room")]
    [InlineData("Home\u2028Server\u2029", "HomeServer")]
    [InlineData("\uD800Silo\uDC00", "Silo")]
    [InlineData("\uFF33\uFF49\uFF4C\uFF4F", "Silo")]
    [InlineData("Cafe\u0301", "Caf\u00E9")]
    [InlineData("\u202E\u200B", "Silo")]
    public void ALabel_LosesWhatCouldReorderOrHideIt_AndIsNormalized(string branding, string expected) =>
        Assert.Equal(expected, LanAdverts.DisplayName(branding, "Silo"));

    [Fact]
    public void ALongLabel_IsNotCutInsideACharacter()
    {
        string label = LanAdverts.DisplayName(string.Concat(Enumerable.Repeat("\U0001F600", 100)), null);

        Assert.Equal(80 * 2 + 1, label.Length);
        Assert.False(char.IsHighSurrogate(label[^2]) && !char.IsLowSurrogate(label[^1]));
    }

    [Theory]
    [InlineData("living room", "L")]
    [InlineData("\u00E9lan", "\u00C9")]
    [InlineData("7th floor", "7")]
    [InlineData("\U0001F600 Home", "S")]
    [InlineData("\uD800", "S")]
    [InlineData("", "S")]
    public void TheMarksLetter_IsAWholeLetterOrDigit(string label, string expected) =>
        Assert.Equal(expected, LanAdverts.Initial(label));
}
