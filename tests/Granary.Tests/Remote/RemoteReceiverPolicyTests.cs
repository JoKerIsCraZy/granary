using System.Net;
using Granary.Core.Remote;
using Granary.Tests.Fakes;

namespace Granary.Tests.Remote;

/// <summary>
/// The receiver's decisions as pure functions, as silo-roku's <c>policy.bs</c> makes them: who may connect, who goes when
/// the connections are full, who is authorized, and the command budget.
/// </summary>
public sealed class RemoteReceiverPolicyTests
{
    private static readonly string ServerId = SiloCastServerId.From(new Uri("https://silo.example"));

    private static readonly ReceiverIdentity Receiver = new(RemoteHarness.ReceiverId, "Elena's PC", ServerId, "Example Server", "deploy-1");

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.178.20", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.255.255", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::1", false)]
    public void OnlyLocalPeers_MayConnect(string address, bool local) =>
        Assert.Equal(local, RemoteReceiverPolicy.IsLocalPeer(IPAddress.Parse(address)));

    [Fact]
    public void BelowFour_EveryoneIsAdmitted()
    {
        var three = new[] { Active(1, 10), Active(2, 20), Active(3, 30) };

        Assert.IsType<Admission.Admit>(RemoteReceiverPolicy.Admit(three));
    }

    [Fact]
    public void TheFifthConnection_EvictsOneThatNeverSaidHello_First()
    {
        var four = new[] { Active(1, 10), Unauthorized(2, 20), Silent(3, 40), Silent(4, 30) };

        Assert.Equal(new Admission.Evict(4), RemoteReceiverPolicy.Admit(four));
    }

    [Fact]
    public void Then_AnUnauthorizedOne()
    {
        var four = new[] { Passive(1, 10), Unauthorized(2, 30), Unauthorized(3, 20), Active(4, 5) };

        Assert.Equal(new Admission.Evict(3), RemoteReceiverPolicy.Admit(four));
    }

    [Fact]
    public void Then_TheOldestPassiveOne()
    {
        var four = new[] { Active(1, 10), Passive(2, 30), Passive(3, 20), Active(4, 5) };

        Assert.Equal(new Admission.Evict(3), RemoteReceiverPolicy.Admit(four));
    }

    [Fact]
    public void FourActiveAuthorizedPhones_RefuseTheNewcomer()
    {
        var four = new[] { Active(1, 10), Active(2, 20), Active(3, 30), Active(4, 40) };

        Assert.IsType<Admission.Refuse>(RemoteReceiverPolicy.Admit(four));
    }

    [Fact]
    public void ANewerHelloOfTheSamePhone_ReplacesItsOtherConnections()
    {
        var existing = new[]
        {
            Active(1, 10) with { DeviceId = "phone-1" },
            Active(2, 20) with { DeviceId = "phone-2" },
            Active(3, 30) with { DeviceId = "phone-1" },
        };

        Assert.Equal([1], RemoteReceiverPolicy.ReplacedBy("phone-1", 3, existing));
        Assert.Empty(RemoteReceiverPolicy.ReplacedBy("", 3, existing));
    }

    [Theory]
    [InlineData("phone", "2", true)]
    [InlineData("tv", "2", false)]
    [InlineData("phone", "1", false)]
    [InlineData("phone", "1,2", true)]
    public void AHello_NeedsTheRolePhone_Version2_AndAServer(string role, string versions, bool accepted)
    {
        var hello = Hello(ServerId, null) with { Role = role, SupportedVersions = [.. versions.Split(',').Select(int.Parse)] };

        Assert.Equal(accepted ? HelloVerdict.Authorized : HelloVerdict.Refused, RemoteReceiverPolicy.Judge(hello, Receiver));
        Assert.Equal(HelloVerdict.Refused, RemoteReceiverPolicy.Judge(Hello(null, "deploy-1"), Receiver));
        Assert.Equal(HelloVerdict.Refused, RemoteReceiverPolicy.Judge(Hello("", "deploy-1"), Receiver));
    }

    [Fact]
    public void ThePhonesServer_MatchesByIdOrByIdentity()
    {
        string otherAddress = SiloCastServerId.From(new Uri("http://192.168.1.10:8090"));
        // The same address spelt with a default port and capitals.
        string sameAddress = SiloCastServerId.From(new Uri("https://SILO.example:443/"));

        Assert.Equal(HelloVerdict.Authorized, RemoteReceiverPolicy.Judge(Hello(sameAddress, null), Receiver));
        Assert.Equal(HelloVerdict.Authorized, RemoteReceiverPolicy.Judge(Hello(otherAddress, "deploy-1"), Receiver));
        Assert.Equal(HelloVerdict.Unauthorized, RemoteReceiverPolicy.Judge(Hello(otherAddress, null), Receiver));
        Assert.Equal(HelloVerdict.Unauthorized, RemoteReceiverPolicy.Judge(Hello(otherAddress, "deploy-2"), Receiver));
        Assert.Equal(HelloVerdict.Unauthorized,
            RemoteReceiverPolicy.Judge(Hello(otherAddress, "deploy-1"), Receiver with { ServerIdentity = null }));
    }

    [Fact]
    public void FortyCommandsAtOnce_Pass_TheNextDoesNot_AndTwentyMoreASecondLater()
    {
        var time = new ManualTimeProvider();
        var budget = new CommandBudget(time);

        Assert.All(Enumerable.Range(0, 40), _ => Assert.True(budget.TryTake()));
        Assert.False(budget.TryTake());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.All(Enumerable.Range(0, 20), _ => Assert.True(budget.TryTake()));
        Assert.False(budget.TryTake());
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(budget.TryTake());
    }

    [Fact]
    public void ARateLimit_IsReportedAtMostOnceASecond()
    {
        var time = new ManualTimeProvider();
        var budget = new CommandBudget(time);

        Assert.True(budget.MayReport());
        Assert.False(budget.MayReport());
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(budget.MayReport());
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(budget.MayReport());
    }

    [Fact]
    public void TheTextsSentToAPhone_AreTheRokuAppsWithThePcInItsPlace()
    {
        Assert.Equal("The PC player is not ready yet.", RemoteTexts.PlayerNotReady);
        Assert.Equal("Too many devices are controlling this PC. Disconnect one and try again.", RemoteTexts.TooManyControllers);
        Assert.Equal("This PC plays from Example Server.", RemoteTexts.PlaysFrom("Example Server"));
        Assert.DoesNotContain(typeof(RemoteTexts).GetFields().Select(field => field.GetValue(null) as string),
            text => text?.Contains("Roku", StringComparison.Ordinal) == true);
    }

    private static SiloCastMessage.PhoneHello Hello(string? serverId, string? serverIdentity) =>
        new("Elena's phone", "phone-1", serverId, "Example Server", serverIdentity, [2], new HashSet<string>(), false, "phone");

    private static ControllerFacts Active(int number, long connectedAt) => new(number, true, true, false, connectedAt);

    private static ControllerFacts Passive(int number, long connectedAt) => new(number, true, true, true, connectedAt);

    private static ControllerFacts Unauthorized(int number, long connectedAt) => new(number, true, false, false, connectedAt);

    private static ControllerFacts Silent(int number, long connectedAt) => new(number, false, false, false, connectedAt);
}
