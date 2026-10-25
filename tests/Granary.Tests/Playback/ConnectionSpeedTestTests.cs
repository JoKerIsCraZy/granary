using System.Net;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>Settings → Test speed: the accurate test against the advertised probe, and its report.</summary>
public sealed class ConnectionSpeedTestTests
{
    private const string ProbePath = "api/v2/playback/bandwidth-probe";
    private const string ResultsPath = "api/v2/playback/bandwidth-probe/results";

    private readonly ManualTimeProvider _time = new();
    private readonly ScriptedSilo _silo = new();
    private readonly FakeNetworkLink _network = new();

    internal static ConnectionSpeedTest SpeedTest(ScriptedSilo silo, TimeProvider time, INetworkLink network)
    {
        var probes = new BandwidthProbeApi(silo.Http, new FakeHttpClients(new FakeHttpMessageHandler((_, _) => throw new InvalidOperationException("No hop"))));
        var sink = new LogSink();
        return new ConnectionSpeedTest(new PlaybackApi(silo.Http), new BandwidthProbe(probes, time, sink.For<BandwidthProbe>()), probes, network,
            sink.For<ConnectionSpeedTest>());
    }

    internal static void Capabilities(ScriptedSilo silo, string? probeUrl = "https://nas.example/silo/api/v2/playback/bandwidth-probe",
        params string[] features)
    {
        string url = probeUrl is null ? "" : $"\"bandwidth_probe_url\": \"{probeUrl}\", \"bandwidth_probe_target_kbps\": 115000, ";
        string listed = string.Join(",", (features.Length == 0 ? ["connection_speed_v1", "buffering_quality_v1"] : features).Select(f => $"\"{f}\""));
        silo.On(HttpMethod.Get, "api/v2/playback/capabilities", ScriptedSilo.Json(HttpStatusCode.OK,
            $$"""{ "revision": "1", "state": "available", "allowed": true, "installation_id": "i", "protocol_versions": [3], {{url}}"features": [{{listed}}] }"""));
    }

    private Task<SpeedTestOutcome> RunAsync(CancellationToken? cancellationToken = null) =>
        SpeedTest(_silo, _time, _network).RunAsync(ScriptedSilo.Base, cancellationToken ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task AMeasuredTest_IsReportedAsTheViewers()
    {
        Capabilities(_silo);
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Body(TimedProbeStream.Steady(_time, 400_000, 11_000)));
        _silo.On(HttpMethod.Post, ResultsPath, ScriptedSilo.NoContent());

        var outcome = await RunAsync();

        Assert.Equal(new SpeedTestOutcome(SpeedTestStatus.Measured, new SpeedTestResult(400_000, false, 150_000_000, 3000)), outcome);
        Assert.Contains("size=1000000000", Assert.Single(_silo.SentTo(HttpMethod.Get, ProbePath)).Uri.Query, StringComparison.Ordinal);
        Assert.Equal("""{"kbps":400000,"lower_bound":false,"bytes":150000000,"duration_ms":3000,"transport":"ethernet","manual":true}""",
            Assert.Single(_silo.SentTo(HttpMethod.Post, ResultsPath)).Body);
    }

    [Fact]
    public async Task TheCapabilities_AreReadFromTheSavedAddress()
    {
        // The server names the probe by the address it was reached at. Over a local route that is the private address,
        // whose key this app trusts only through the route; read from the saved address, the probe is beneath it and
        // still takes the route.
        Capabilities(_silo);
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Body(TimedProbeStream.Steady(_time, 50_000, 11_000)));
        _silo.On(HttpMethod.Post, ResultsPath, ScriptedSilo.NoContent());

        await RunAsync();

        Assert.True(Assert.Single(_silo.SentTo(HttpMethod.Get, "api/v2/playback/capabilities")).SavedAddressOnly);
        Assert.False(Assert.Single(_silo.SentTo(HttpMethod.Get, ProbePath)).SavedAddressOnly);
    }

    [Fact]
    public async Task TheTransport_IsTheLinksOwn()
    {
        _network.Current = new NetworkLink(NetworkTransports.Wifi, false);
        Capabilities(_silo);
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Body(TimedProbeStream.Steady(_time, 50_000, 11_000)));
        _silo.On(HttpMethod.Post, ResultsPath, ScriptedSilo.NoContent());

        await RunAsync();

        Assert.Contains("\"transport\":\"wifi\"", Assert.Single(_silo.SentTo(HttpMethod.Post, ResultsPath)).Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NetworkTransports.Cellular, false)]
    [InlineData(NetworkTransports.Wifi, true)]
    public async Task ACellularOrMeteredLink_IsNotTested(string transport, bool metered)
    {
        _network.Current = new NetworkLink(transport, metered);

        var outcome = await RunAsync();

        Assert.Equal(SpeedTestStatus.Metered, outcome.Status);
        Assert.Empty(_silo.Sent);
    }

    [Fact]
    public async Task AResultTheServerRefuses_IsNotSaved()
    {
        Capabilities(_silo);
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Body(TimedProbeStream.Steady(_time, 50_000, 11_000)));
        _silo.On(HttpMethod.Post, ResultsPath, ScriptedSilo.Problem(422, "validation_failed"));

        var outcome = await RunAsync();

        Assert.Equal(SpeedTestStatus.NotSaved, outcome.Status);
    }

    [Fact]
    public async Task ARateLimit_IsPassedOn()
    {
        Capabilities(_silo);
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.RateLimited(TimeSpan.FromSeconds(7)));

        var outcome = await RunAsync();

        Assert.Equal(new SpeedTestOutcome(SpeedTestStatus.RateLimited, RetryAfterSeconds: 7), outcome);
        Assert.Empty(_silo.SentTo(HttpMethod.Post, ResultsPath));
    }

    [Theory]
    [InlineData(true, "buffering_quality_v1")]
    [InlineData(false, "connection_speed_v1")]
    public async Task AServerWithoutAProbe_MeasuresNothing(bool advertisesUrl, string feature)
    {
        Capabilities(_silo, advertisesUrl ? "https://nas.example/silo/api/v2/playback/bandwidth-probe" : null, feature);

        var outcome = await RunAsync();

        Assert.Equal(SpeedTestStatus.Failed, outcome.Status);
        Assert.Empty(_silo.SentTo(HttpMethod.Get, ProbePath));
    }

    [Fact]
    public async Task ATransferThatMeasuredNothing_Failed()
    {
        Capabilities(_silo);
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Body(new TimedProbeStream(_time) { EndMs = 300 }.Chunk(0, 1000)));

        var outcome = await RunAsync();

        Assert.Equal(SpeedTestStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task ASecondRun_JoinsTheOneRunning()
    {
        var capabilities = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.On(HttpMethod.Get, "api/v2/playback/capabilities", () => capabilities.Task);
        var test = SpeedTest(_silo, _time, _network);

        var first = test.RunAsync(ScriptedSilo.Base, TestContext.Current.CancellationToken);
        var second = test.RunAsync(ScriptedSilo.Base, TestContext.Current.CancellationToken);
        capabilities.SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await first;

        Assert.Same(first, second);
        Assert.Single(_silo.SentTo(HttpMethod.Get, "api/v2/playback/capabilities"));
    }

    [Fact]
    public async Task AStoppedTest_SaysSo()
    {
        Capabilities(_silo);
        using var stop = new CancellationTokenSource();
        var body = TimedProbeStream.Steady(_time, 50_000, 11_000);
        _silo.On(HttpMethod.Get, ProbePath, () =>
        {
            stop.Cancel();
            return ProbeAnswers.Body(body)();
        });

        var outcome = await RunAsync(stop.Token);

        Assert.Equal(SpeedTestStatus.Stopped, outcome.Status);
        Assert.Empty(_silo.SentTo(HttpMethod.Post, ResultsPath));
    }

    [Theory]
    [InlineData("https://nas.example/silo/api/v2/playback/bandwidth-probe", "https://nas.example/silo/api/v2/playback/bandwidth-probe?size=1000000000")]
    [InlineData("https://nas.example/silo/api/v2/playback/bandwidth-probe?size=5&media_path=true",
        "https://nas.example/silo/api/v2/playback/bandwidth-probe?media_path=true&size=1000000000")]
    [InlineData("api/v2/playback/bandwidth-probe", "https://nas.example/silo/api/v2/playback/bandwidth-probe?size=1000000000")]
    [InlineData("/api/v2/playback/bandwidth-probe", "https://nas.example/api/v2/playback/bandwidth-probe?size=1000000000")]
    public void TheProbe_IsTheAdvertisedOne_AskingForTheTestsBytes(string advertised, string expected) =>
        Assert.Equal(new Uri(expected), ConnectionSpeedTest.ProbeUrl(ScriptedSilo.Base, advertised, SpeedTestMode.Accurate));

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("ftp://nas.example/probe")]
    public void NoUsableProbe_IsNone(string? advertised) =>
        Assert.Null(ConnectionSpeedTest.ProbeUrl(ScriptedSilo.Base, advertised, SpeedTestMode.Accurate));
}
