using System.Net;
using Granary.Core.Api.Discovery;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Servers;

/// <summary>Finding the active server's local route: through the saved address, private addresses only, verified.</summary>
public sealed class LocalRouteMonitorTests : IDisposable
{
    private static readonly Uri Saved = new("https://silo.example/");
    private static readonly Uri Connections = new("https://silo.example/api/v2/system/connections");

    private readonly ScriptedSilo _silo = new();
    private readonly LocalRoutes _routes = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeNetwork _network = new();
    private readonly FakeLanBrowser _browser = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly FakeSessionManager _session = new()
    {
        State = SessionState.Ready,
        ActiveServer = ScriptedSilo.Entry("srv1", Saved) with { ServerId = "server-1" },
    };

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AVerifiedPrivateAddress_BecomesTheRoute()
    {
        Answer("http://192.168.1.20:8080");
        var monitor = Create();

        await monitor.CheckAsync(Cancellation);

        // HTTPS on the same port, whatever scheme the local URL names.
        Assert.Equal(["https://192.168.1.20:8080/"], _probe.Tried);
        Assert.Equal("https://192.168.1.20:8080/", _routes.Find(Saved)!.Origin.AbsoluteUri);
        Assert.Equal("pin-1", _probe.Pins.Single());
    }

    [Fact]
    public async Task AName_IsReducedToItsPrivateIPv4Addresses()
    {
        Answer("http://nas.lan:8080");
        _network.Addresses["nas.lan"] = [IPAddress.Parse("8.8.8.8"), IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Parse("10.0.0.5"), IPAddress.Parse("172.20.1.2")];
        // None answers here, so every candidate is tried in turn.
        _probe.Reachable = false;
        var monitor = Create();

        await monitor.CheckAsync(Cancellation);

        Assert.Equal(["https://10.0.0.5:8080/", "https://172.20.1.2:8080/"], _probe.Tried);
    }

    [Fact]
    public async Task ANameWithManyAddresses_IsTriedAtFourOfThem()
    {
        // The server names the local address; a check stays small whatever it resolves to.
        Answer("http://nas.lan:8080");
        _network.Addresses["nas.lan"] = [.. Enumerable.Range(1, 10).Select(i => IPAddress.Parse($"10.0.0.{i}"))];
        _probe.Reachable = false;
        var monitor = Create();

        await monitor.CheckAsync(Cancellation);

        Assert.Equal(4, _probe.Tried.Count);
    }

    [Theory]
    [InlineData("10.255.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.169.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    public void OnlyRfc1918AddressesArePrivate(string address, bool isPrivate) =>
        Assert.Equal(isPrivate, LocalRouteMonitor.IsPrivateIPv4(IPAddress.Parse(address)));

    [Fact]
    public async Task ACandidateThatFails_IsNoRoute()
    {
        Answer("http://192.168.1.20:8080");
        _probe.Reachable = false;
        var monitor = Create();

        await monitor.CheckAsync(Cancellation);

        Assert.Null(_routes.Find(Saved));
    }

    [Fact]
    public async Task ASavedAddressThatAnswersAsAnotherServer_GetsNoRoute()
    {
        _silo.On(HttpMethod.Get, Connections, Json("""{ "server_id": "server-9", "local_route": { "spki_sha256": "pin-1", "url": "http://192.168.1.20:8080" } }"""));
        var monitor = Create();

        await monitor.CheckAsync(Cancellation);

        Assert.Empty(_probe.Tried);
        Assert.Null(_routes.Find(Saved));
    }

    [Fact]
    public async Task WithoutALocalRoute_TheRouteIsDropped()
    {
        Answer("http://192.168.1.20:8080");
        var monitor = Create();
        await monitor.CheckAsync(Cancellation);

        _silo.On(HttpMethod.Get, Connections, Json("""{ "server_id": "server-1" }"""));
        await monitor.CheckAsync(Cancellation);

        Assert.Null(_routes.Find(Saved));
    }

    [Fact]
    public async Task AnUnreachableSavedAddress_KeepsTheVerifiedRoute()
    {
        // No internet at home: the route still presents the key it was verified with.
        Answer("http://192.168.1.20:8080");
        var monitor = Create();
        await monitor.CheckAsync(Cancellation);

        _silo.On(HttpMethod.Get, Connections, ScriptedSilo.NetworkError());
        await monitor.CheckAsync(Cancellation);

        Assert.NotNull(_routes.Find(Saved));
    }

    [Theory]
    [InlineData("http://silo.example/")]
    [InlineData("https://silo.example/silo")]
    public async Task APlainHttpOrPrefixedSavedAddress_GetsNoRoute(string saved)
    {
        // Plain HTTP cannot vouch for a key; the local address serves the API at its root.
        _session.ActiveServer = ScriptedSilo.Entry("srv1", new Uri(saved));
        var monitor = Create();

        await monitor.CheckAsync(Cancellation);

        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task SigningOut_DropsTheRoute()
    {
        Answer("http://192.168.1.20:8080");
        var monitor = Create();
        await monitor.CheckAsync(Cancellation);

        _session.State = SessionState.SignedOut;
        await monitor.CheckAsync(Cancellation);

        Assert.Null(_routes.Find(Saved));
    }

    [Fact]
    public async Task ANetworkChange_ChecksAgain_OnceItSettles()
    {
        Answer("http://192.168.1.20:8080");
        var monitor = Create();
        monitor.Start();
        await _time.FireNextTimerAsync(Cancellation);
        await Until(() => _routes.Find(Saved) is not null);

        _network.RaiseChanged();
        _network.RaiseChanged();
        _time.Advance(TimeSpan.FromSeconds(2));

        await Until(() => _silo.Sent.Count == 2);
        monitor.Dispose();
    }

    [Fact]
    public async Task TheServersAdvert_IsACandidate_OverHttpsOnItsPort()
    {
        // No address from the administrator: the advert alone names one.
        AnswerPinOnly();
        var monitor = Create();
        await monitor.CheckAsync(Cancellation);
        Assert.True(_browser.IsBrowsing);

        _browser.Advertise("a", "server-1", 8090, "Silo", "192.168.1.30", "fd00::30");
        await monitor.CheckAsync(Cancellation);

        Assert.Equal(["https://192.168.1.30:8090/"], _probe.Tried);
        Assert.Equal("https://192.168.1.30:8090/", _routes.Find(Saved)!.Origin.AbsoluteUri);
    }

    [Fact]
    public async Task TheAdministratorsAddress_IsTriedBeforeTheAdverts()
    {
        Answer("http://192.168.1.20:8080");
        _probe.Reachable = false;
        var monitor = Create();
        await monitor.CheckAsync(Cancellation);
        _browser.Advertise("a", "server-1", 8090, "Silo", "192.168.1.30");

        await monitor.CheckAsync(Cancellation);

        Assert.Equal(["https://192.168.1.20:8080/", "https://192.168.1.20:8080/", "https://192.168.1.30:8090/"], _probe.Tried);
    }

    [Fact]
    public async Task AnotherServersAdvert_OrAPublicAddress_IsNoCandidate()
    {
        AnswerPinOnly();
        var monitor = Create();
        await monitor.CheckAsync(Cancellation);

        _browser.Advertise("a", "server-9", 8090, "Silo", "192.168.1.31");
        _browser.Advertise("b", "server-1", 8090, "Silo", "8.8.8.8");
        _browser.Advertise("c", null, 8090, "Silo", "192.168.1.32");
        await monitor.CheckAsync(Cancellation);

        Assert.Empty(_probe.Tried);
        Assert.Null(_routes.Find(Saved));
    }

    [Fact]
    public async Task AnAdvertThatComes_WhileThereIsNoRoute_ChecksAgainOnceItSettles()
    {
        AnswerPinOnly();
        var monitor = Create();
        monitor.Start();
        await _time.FireNextTimerAsync(Cancellation);
        await Until(() => _browser.IsBrowsing);

        _browser.Advertise("a", "server-1", 8090, "Silo", "192.168.1.30");
        _time.Advance(TimeSpan.FromSeconds(2));

        await Until(() => _routes.Find(Saved) is not null);
        monitor.Dispose();
        Assert.False(_browser.IsBrowsing);
    }

    [Fact]
    public async Task WithoutAPin_NothingIsBrowsed()
    {
        _silo.On(HttpMethod.Get, Connections, Json("""{ "server_id": "server-1" }"""));
        var monitor = Create();

        await monitor.CheckAsync(Cancellation);

        Assert.Equal(0, _browser.Starts);
    }

    [Fact]
    public async Task SigningOut_StopsBrowsing()
    {
        AnswerPinOnly();
        var monitor = Create();
        await monitor.CheckAsync(Cancellation);

        _session.State = SessionState.SignedOut;
        await monitor.CheckAsync(Cancellation);

        Assert.False(_browser.IsBrowsing);
    }

    [Fact]
    public async Task ABrowseThatStopped_StartsAgainWithTheNextCheck()
    {
        AnswerPinOnly();
        var monitor = Create();
        await monitor.CheckAsync(Cancellation);

        _browser.Fail();
        await monitor.CheckAsync(Cancellation);

        Assert.Equal(2, _browser.Starts);
    }

    [Fact]
    public async Task LaterAdvertsWithTheServersId_DoNotCrowdOutTheFirst()
    {
        // Anyone can advertise the id: four 10.x adverts after the genuine 192.168.x one must not push it out.
        AnswerPinOnly();
        _probe.Reachable = false;
        var monitor = Create();
        await monitor.CheckAsync(Cancellation);
        _browser.Advertise("genuine", "server-1", 8090, "Silo", "192.168.1.30");
        for (int i = 1; i <= 4; i++)
        {
            _browser.Advertise($"spoof{i}", "server-1", 8090, "Silo", $"10.0.0.{i}");
        }

        await monitor.CheckAsync(Cancellation);

        Assert.Equal(["https://192.168.1.30:8090/", "https://10.0.0.1:8090/", "https://10.0.0.2:8090/", "https://10.0.0.3:8090/"], _probe.Tried);
    }

    [Fact]
    public async Task TheOriginLastConfirmed_IsTriedFirst()
    {
        AnswerPinOnly();
        var monitor = Create();
        await monitor.CheckAsync(Cancellation);
        for (int i = 1; i <= 4; i++)
        {
            _browser.Advertise($"spoof{i}", "server-1", 8090, "Silo", $"10.0.0.{i}");
        }
        _probe.Reachable = false;
        await monitor.CheckAsync(Cancellation);
        _browser.Advertise("genuine", "server-1", 8090, "Silo", "192.168.1.30");
        _probe.Reachable = true;
        _probe.Only = "https://192.168.1.30:8090/";
        // Four spoofs fill the window before it, so it is not even a candidate yet; it comes when one goes.
        _browser.Lose("spoof4");
        await monitor.CheckAsync(Cancellation);
        Assert.Equal("https://192.168.1.30:8090/", _routes.Find(Saved)!.Origin.AbsoluteUri);

        // The route drops (a request on it failed); the next check starts with the origin that presented the key.
        _routes.Drop(Saved);
        _browser.Advertise("spoof4", "server-1", 8090, "Silo", "10.0.0.4");
        _probe.Tried.Clear();
        await monitor.CheckAsync(Cancellation);

        Assert.Equal("https://192.168.1.30:8090/", _probe.Tried[0]);
    }

    [Fact]
    public void OnlyTheWatchedServersAdverts_AreKept_AtMostEight()
    {
        var watch = new LanAdvertWatch(_browser, _logs.For<LanAdvertWatch>());
        watch.Watch("server-1");

        for (int i = 1; i <= 20; i++)
        {
            _browser.Advertise($"other{i}", "server-9", 8090, "Silo", $"192.168.3.{i}");
            _browser.Advertise($"ours{i}", "server-1", 8090, "Silo", $"192.168.4.{i}");
        }

        Assert.Equal(["https://192.168.4.1:8090/", "https://192.168.4.2:8090/", "https://192.168.4.3:8090/", "https://192.168.4.4:8090/"],
            watch.Origins("server-1").Select(o => o.AbsoluteUri));
        Assert.Empty(watch.Origins("server-9"));
        // Past eight, later adverts are dropped until one goes.
        _browser.Lose("ours1");
        _browser.Lose("ours2");
        _browser.Lose("ours3");
        _browser.Lose("ours4");
        _browser.Lose("ours5");
        Assert.Equal(["https://192.168.4.6:8090/", "https://192.168.4.7:8090/", "https://192.168.4.8:8090/"],
            watch.Origins("server-1").Select(o => o.AbsoluteUri));
    }

    [Fact]
    public async Task AdvertChurn_ChecksAtMostEveryThirtySeconds()
    {
        AnswerPinOnly();
        _probe.Reachable = false;
        var monitor = Create();
        monitor.Start();
        await _time.FireNextTimerAsync(Cancellation);
        await Until(() => _browser.IsBrowsing && _silo.Sent.Count == 1);

        _browser.Advertise("a", "server-1", 8090, "Silo", "192.168.1.30");
        _time.Advance(TimeSpan.FromSeconds(2));
        await Until(() => _silo.Sent.Count == 2);

        // More adverts: no check before thirty seconds after the last, and they do not put it off.
        _browser.Advertise("b", "server-1", 8090, "Silo", "192.168.1.31");
        _time.Advance(TimeSpan.FromSeconds(10));
        _browser.Advertise("c", "server-1", 8090, "Silo", "192.168.1.32");
        _time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50, Cancellation);
        Assert.Equal(2, _silo.Sent.Count);

        _time.Advance(LocalRouteMonitor.AdvertRecheckInterval - TimeSpan.FromSeconds(20));
        await Until(() => _silo.Sent.Count == 3);
        monitor.Dispose();
    }

    public void Dispose() => _routes.Dispose();

    private LocalRouteMonitor Create() =>
        new(new SystemApi(_silo.Http), _session, _routes, _probe, _network, new LanAdvertWatch(_browser, _logs.For<LanAdvertWatch>()), _time,
            _logs.For<LocalRouteMonitor>());

    private void AnswerPinOnly() => _silo.On(HttpMethod.Get, Connections,
        Json("""{ "server_id": "server-1", "current": { "kind": "default" }, "endpoints": [], "local_route": { "spki_sha256": "pin-1" } }"""));

    private void Answer(string localUrl) => _silo.On(HttpMethod.Get, Connections,
        Json($$"""{ "server_id": "server-1", "current": { "kind": "default" }, "endpoints": [], "local_route": { "spki_sha256": "pin-1", "url": "{{localUrl}}" } }"""));

    private static Func<Task<HttpResponseMessage>> Json(string json) => ScriptedSilo.Json(HttpStatusCode.OK, json);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(5, Cancellation);
        }
    }

    private sealed class FakeProbe : ILocalRouteProbe
    {
        public List<string> Tried { get; } = [];

        public List<string> Pins { get; } = [];

        public bool Reachable { get; set; } = true;

        /// <summary>When set, the only candidate that presents the key.</summary>
        public string? Only { get; set; }

        public Task<LocalRoute?> ProbeAsync(Uri savedOrigin, Uri candidate, string pin, string serverId, CancellationToken cancellationToken)
        {
            Tried.Add(candidate.AbsoluteUri);
            Pins.Add(pin);
            return Task.FromResult(Reachable && serverId == "server-1" && (Only is null || Only == candidate.AbsoluteUri)
                ? new LocalRoute(savedOrigin, candidate, pin, new HttpMessageInvoker(new HttpClientHandler()))
                : null);
        }
    }

    private sealed class FakeNetwork : ILocalNetwork
    {
        public Dictionary<string, IPAddress[]> Addresses { get; } = [];

        public event EventHandler? Changed;

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(Addresses.TryGetValue(host, out var addresses) ? addresses : []);

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
