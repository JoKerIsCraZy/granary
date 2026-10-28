using Granary.Core.Servers;
using Granary.Tests.Fakes;

namespace Granary.Tests.Servers;

/// <summary>Listing the servers advertised on this network: only those whose identity answers the advert's id.</summary>
public sealed class LanServerDiscoveryTests
{
    private const string Lan = "http://192.168.1.6:8090/";

    private readonly FakeLanBrowser _browser = new();
    private readonly FakeLanProbe _probe = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly LanServerDiscovery _discovery;

    public LanServerDiscoveryTests()
    {
        _discovery = new LanServerDiscovery(_browser, _probe, _time, _logs.For<LanServerDiscovery>());
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAdvertWhoseIdentityMatches_IsListed_ByItsBrandingName()
    {
        _probe.Ids[Lan] = "srv-1";
        _probe.Names[Lan] = "Living room";
        _discovery.Start();

        _browser.Advertise("a", "srv-1");

        var server = Assert.Single(await ListedAsync(1));
        Assert.Equal(new DiscoveredServer("srv-1", "Living room", new Uri(Lan)), server);
        Assert.Equal("On this network · 192.168.1.6:8090", server.Detail);
    }

    [Fact]
    public async Task WithoutABrandingName_TheInstanceNameLabelsIt()
    {
        _probe.Ids[Lan] = "srv-1";
        _discovery.Start();

        _browser.Advertise("a", "srv-1", instance: "Silo (2)");

        Assert.Equal("Silo (2)", Assert.Single(await ListedAsync(1)).Name);
    }

    [Fact]
    public async Task AnAdvertWhoseIdentityDiffers_IsNotListed_AfterASecondTry()
    {
        // Anyone on the network can advertise any id.
        _probe.Ids[Lan] = "srv-9";
        _discovery.Start();

        _browser.Advertise("a", "srv-1");
        await _time.WaitUntilAsync(() => _time.NextDue == LanServerDiscovery.RetryDelay, Cancellation);
        _time.Advance(LanServerDiscovery.RetryDelay);
        await Until(() => _probe.IdentityCalls(Lan) == 2);

        Assert.Empty(_discovery.Servers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task AnAdvertWithoutAUsableId_IsNeverAsked(string? id)
    {
        _discovery.Start();

        _browser.Advertise("a", id);
        await Task.Delay(50, Cancellation);

        Assert.Empty(_probe.Asked);
    }

    [Fact]
    public async Task AnAdvertWithOnlyUnusableAddresses_IsNeverAsked()
    {
        _discovery.Start();

        _browser.Advertise("a", "srv-1", 8090, "Silo", "fe80::1%4", "127.0.0.1");
        await Task.Delay(50, Cancellation);

        Assert.Empty(_probe.Asked);
    }

    [Fact]
    public async Task OneServerOnTwoInterfaces_IsOneRow()
    {
        _probe.Ids[Lan] = "srv-1";
        _probe.Ids["http://10.0.0.6:8090/"] = "srv-1";
        _discovery.Start();

        _browser.Advertise("a", "srv-1", 8090, "Silo", "192.168.1.6");
        _browser.Advertise("b", "srv-1", 8090, "Silo", "10.0.0.6");
        // Both confirmed, one row: the lower address of the two.
        await Until(() => _discovery.Servers is [{ Url.AbsoluteUri: "http://10.0.0.6:8090/" }]);
        await Task.Delay(50, Cancellation);
        Assert.Equal("http://10.0.0.6:8090/", Assert.Single(_discovery.Servers).Url.AbsoluteUri);
    }

    [Fact]
    public async Task TwoServers_AreListedByName()
    {
        _probe.Ids[Lan] = "srv-1";
        _probe.Names[Lan] = "Zoo";
        _probe.Ids["http://192.168.1.7:8090/"] = "srv-2";
        _probe.Names["http://192.168.1.7:8090/"] = "attic";
        _discovery.Start();

        _browser.Advertise("a", "srv-1", 8090, "Silo", "192.168.1.6");
        _browser.Advertise("b", "srv-2", 8090, "Silo", "192.168.1.7");

        Assert.Equal(["attic", "Zoo"], (await ListedAsync(2)).Select(s => s.Name));
    }

    [Fact]
    public async Task AnAdvertThatGoes_TakesItsRowAlong()
    {
        _probe.Ids[Lan] = "srv-1";
        _discovery.Start();
        _browser.Advertise("a", "srv-1");
        await ListedAsync(1);

        _browser.Lose("a");

        Assert.Empty(_discovery.Servers);
    }

    [Fact]
    public async Task Stop_ForgetsTheRows_AndDropsWhatTheOldBrowseStillSays()
    {
        _probe.Ids[Lan] = "srv-1";
        _discovery.Start();
        _browser.Advertise("a", "srv-1");
        await ListedAsync(1);
        var old = _browser.Current!;

        _discovery.Stop();
        old.Resolved("b", FakeLanBrowser.Advert("srv-1"));
        await Task.Delay(50, Cancellation);

        Assert.Empty(_discovery.Servers);
        Assert.Equal(1, _browser.Stops);
        Assert.Equal(1, _probe.IdentityCalls(Lan));
    }

    [Fact]
    public async Task ABrowseThatFails_StartsAgainShortlyOnce_ThenWithTheRefresh()
    {
        _discovery.Start();

        _browser.Fail();
        _time.Advance(LanServerDiscovery.BrowseRetryDelay);
        Assert.Equal(2, _browser.Starts);

        _browser.Fail();
        _time.Advance(LanServerDiscovery.BrowseRetryDelay);
        Assert.Equal(2, _browser.Starts);

        _time.Advance(LanServerDiscovery.RefreshInterval);
        await Until(() => _browser.Starts == 3);
    }

    [Fact]
    public async Task AListedRow_StaysThroughTwoFailedRefreshes_AndGoesWithTheThird()
    {
        _probe.Ids[Lan] = "srv-1";
        _discovery.Start();
        _browser.Advertise("a", "srv-1");
        await ListedAsync(1);
        _probe.Ids[Lan] = null;

        for (int refresh = 1; refresh <= LanServerDiscovery.MaxMisses; refresh++)
        {
            Assert.Single(_discovery.Servers);
            _time.Advance(LanServerDiscovery.RefreshInterval);
            await Until(() => _probe.IdentityCalls(Lan) == 1 + refresh);
            await Task.Delay(20, Cancellation);
        }

        Assert.Empty(_discovery.Servers);
    }

    [Fact]
    public async Task ChangesAreTold()
    {
        _probe.Ids[Lan] = "srv-1";
        int changes = 0;
        _discovery.Changed += (_, _) => Interlocked.Increment(ref changes);
        _discovery.Start();

        _browser.Advertise("a", "srv-1");
        await ListedAsync(1);

        Assert.True(changes >= 1);
    }

    private async Task<IReadOnlyList<DiscoveredServer>> ListedAsync(int count)
    {
        await Until(() => _discovery.Servers.Count == count);
        return _discovery.Servers;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(5, Cancellation);
        }
    }
}
