using Granary.Core.Servers;

namespace Granary.Tests.Servers;

public sealed class ServerAddressTests
{
    public static TheoryData<string, string[]> Inputs => new()
    {
        { "silo.example.org", ["https://silo.example.org", "http://silo.example.org", "http://silo.example.org:8090"] },
        { "  Silo.Example.ORG  ", ["https://silo.example.org", "http://silo.example.org", "http://silo.example.org:8090"] },
        { "silo.example.org:8443", ["https://silo.example.org:8443", "http://silo.example.org:8443"] },
        { "https://Silo.Example.org/", ["https://silo.example.org"] },
        { "http://nas:8090/silo/", ["http://nas:8090/silo"] },
        { "nas/silo", ["https://nas/silo", "http://nas/silo", "http://nas:8090/silo"] },
        { "192.168.1.10", ["https://192.168.1.10", "http://192.168.1.10", "http://192.168.1.10:8090"] },
        { "fe80::1", ["https://[fe80::1]", "http://[fe80::1]", "http://[fe80::1]:8090"] },
        { "[::1]:8090", ["https://[::1]:8090", "http://[::1]:8090"] },
    };

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Candidates_FollowTheAndroidOrder(string input, string[] expected)
    {
        var candidates = ServerAddress.Candidates(input);

        Assert.Equal(expected, candidates.Select(c => c.OriginalString));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ftp://silo.example.org")]
    [InlineData("file:///C:/videos")]
    public void Candidates_RejectsNonServerInput(string? input)
    {
        Assert.Empty(ServerAddress.Candidates(input));
    }
}
