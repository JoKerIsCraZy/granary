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

    public void Dispose() => _routes.Dispose();

    private LocalRouteMonitor Create() =>
        new(new SystemApi(_silo.Http), _session, _routes, _probe, _network, _time, _logs.For<LocalRouteMonitor>());

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

        public Task<LocalRoute?> ProbeAsync(Uri savedOrigin, Uri candidate, string pin, string serverId, CancellationToken cancellationToken)
        {
            Tried.Add(candidate.AbsoluteUri);
            Pins.Add(pin);
            return Task.FromResult(Reachable && serverId == "server-1"
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
