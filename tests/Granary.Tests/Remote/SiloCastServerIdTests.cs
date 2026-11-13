using System.Buffers.Text;
using System.Text;
using Granary.Core.Remote;

namespace Granary.Tests.Remote;

/// <summary>
/// The server id Silo's phones key a server by, which is its address, and how they compare two of them. The cases are
/// silo-android's <c>AndroidServerIdentityTest.kt</c>; only its addresses and expectations are taken.
/// </summary>
public sealed class SiloCastServerIdTests
{
    [Fact]
    public void AnId_IsTheAddressInBase64Url_WithoutPaddingOrATrailingSlash()
    {
        string id = SiloCastServerId.From(new Uri("https://silo.example/"));

        Assert.Equal(Wire("https://silo.example"), id);
        Assert.DoesNotContain('=', id);
        // A base path keeps its path, without the slash after it.
        Assert.Equal(Wire("https://silo.example/media"), SiloCastServerId.From(new Uri("https://silo.example/media/")));
    }

    [Fact]
    public void AnIdFromAUri_HasSchemeAndHostInLowerCase_AndNoDefaultPort()
    {
        Assert.Equal(Wire("https://silo.example"), SiloCastServerId.From(new Uri("HTTPS://Silo.Example:443/")));
        Assert.Equal(Wire("http://silo.example:8090"), SiloCastServerId.From(new Uri("http://Silo.Example:8090")));
    }

    [Fact]
    public void Ids_MatchAcrossSchemeAndHostCase_AndDefaultPorts()
    {
        // matchesSchemeAndHostCaseAndDefaultPorts: the ids differ as text, the addresses do not.
        string phone = Wire("https://Media.Example.test/library");
        string receiver = Wire("HTTPS://media.example.test:443/library/");
        Assert.NotEqual(phone, receiver);
        Assert.True(SiloCastServerId.IdsMatch(phone, receiver));
        Assert.True(SiloCastServerId.IdsMatch(Wire("http://MEDIA.example.test:80/library"), Wire("http://media.example.test/library")));
        // preservesCredentialsPathQueryFragmentAndNonDefaultPort: the same address with the host in another case, and
        // a host outside ASCII.
        Assert.True(SiloCastServerId.IdsMatch(
            Wire("https://User:Pass@MEDIA.example.test:8443/Library?mode=A#Top"),
            Wire("HTTPS://User:Pass@media.example.test:8443/Library?mode=A#Top")));
        Assert.True(SiloCastServerId.IdsMatch(
            Wire("https://User:Pass@MÉDIA.example.test:443/Library"),
            Wire("HTTPS://User:Pass@média.example.test/Library")));
        // Granary's own id against a phone's spelling of the same address.
        Assert.True(SiloCastServerId.IdsMatch(SiloCastServerId.From(new Uri("https://silo.example")), Wire("HTTPS://Silo.Example:443")));
    }

    [Theory]
    [InlineData("https://user:Pass@media.example.test:8443/Library?mode=A#Top")]
    [InlineData("https://User:Pass@media.example.test:443/Library?mode=A#Top")]
    [InlineData("https://User:Pass@media.example.test:8443/library?mode=A#Top")]
    [InlineData("https://User:Pass@media.example.test:8443/Library?mode=a#Top")]
    [InlineData("https://User:Pass@media.example.test:8443/Library?mode=A#top")]
    public void Credentials_Path_Query_Fragment_AndAnotherPort_TellAddressesApart(string other)
    {
        string canonical = Wire("https://User:Pass@MEDIA.example.test:8443/Library?mode=A#Top");

        Assert.False(SiloCastServerId.IdsMatch(canonical, Wire(other)));
    }

    [Fact]
    public void OnlyAnIdThatEncodesBackToItself_IsReadAsAnAddress()
    {
        // decoderRequiresAnExactRoundTrippingHttpUrl
        const string original = "https://Média.example.test:443/silo?mode=A#top";
        string id = Wire(original);

        Assert.Equal(original, SiloCastServerId.AddressOf(id));
        Assert.Null(SiloCastServerId.AddressOf("not-a-registry-id"));
        Assert.Null(SiloCastServerId.AddressOf("srv"));
        Assert.Null(SiloCastServerId.AddressOf(Wire("srv")));
        Assert.Null(SiloCastServerId.AddressOf(Wire("file:///tmp/silo")));
        Assert.Null(SiloCastServerId.AddressOf(Wire("ftp://media.example.test")));
        // An id of an address with its trailing slash, and an id with padding: neither is what a phone makes.
        Assert.Null(SiloCastServerId.AddressOf(Base64Url.EncodeToString(Encoding.UTF8.GetBytes("https://media.example.test/"))));
        Assert.Null(SiloCastServerId.AddressOf(id + "="));
        Assert.Null(SiloCastServerId.AddressOf(""));
    }

    [Fact]
    public void UnknownIds_MatchOnlyWhenEqual()
    {
        // exactUnknownIdsMatchButMissingOrDistinctIdsDoNot
        Assert.True(SiloCastServerId.IdsMatch("future-format", "future-format"));
        Assert.False(SiloCastServerId.IdsMatch("future-format-a", "future-format-b"));
        Assert.False(SiloCastServerId.IdsMatch(null, "future-format"));
        Assert.False(SiloCastServerId.IdsMatch("future-format", null));
        Assert.False(SiloCastServerId.IdsMatch("", ""));
    }

    [Fact]
    public void Servers_AlsoMatchByIdentity_WhenBothSidesKnowOne()
    {
        string here = SiloCastServerId.From(new Uri("https://silo.example"));
        string elsewhere = Wire("http://192.168.1.10:8090");

        Assert.True(SiloCastServerId.ServersMatch(here, "deploy-1", elsewhere, "deploy-1"));
        Assert.True(SiloCastServerId.ServersMatch(here, " deploy-1 ", elsewhere, "deploy-1"));
        Assert.True(SiloCastServerId.ServersMatch(here, null, Wire("https://Silo.Example"), null));
        Assert.False(SiloCastServerId.ServersMatch(here, "deploy-1", elsewhere, "deploy-2"));
        Assert.False(SiloCastServerId.ServersMatch(here, "deploy-1", elsewhere, null));
        Assert.False(SiloCastServerId.ServersMatch(here, null, elsewhere, "deploy-1"));
        Assert.False(SiloCastServerId.ServersMatch(here, "", elsewhere, ""));
        Assert.False(SiloCastServerId.ServersMatch(here, "  ", elsewhere, "  "));
    }

    // The id a phone makes of an address it saved (AndroidServerIdentityTest.wireId): trimmed, then base64url.
    private static string Wire(string address) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(address.Trim().TrimEnd('/')));
}
