using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>The speed test download: its timing on the clock, the redirect's one hop, and the answers that are not bytes.</summary>
public sealed class BandwidthProbeTests
{
    private const string ProbePath = "api/v2/playback/bandwidth-probe";
    private static readonly Uri ProbeUrl = ScriptedSilo.Url(ProbePath + "?size=1000000000");

    private readonly ManualTimeProvider _time = new();
    private readonly ScriptedSilo _silo = new();
    private readonly FakeHttpMessageHandler _hop;
    private readonly FakeHttpClients _clients;
    private Func<Task<HttpResponseMessage>> _hopAnswer = () => throw new InvalidOperationException("No hop was expected");

    public BandwidthProbeTests()
    {
        _hop = new FakeHttpMessageHandler((_, _) => _hopAnswer());
        _clients = new FakeHttpClients(_hop);
    }

    private BandwidthProbe Probe() => new(new BandwidthProbeApi(_silo.Http, _clients), _time, new LogSink().For<BandwidthProbe>());

    private Task<ProbeReading> MeasureAsync(SpeedTestMode mode, long? targetKbps = null) =>
        Probe().MeasureAsync(ScriptedSilo.Base, ProbeUrl, mode, targetKbps, TestContext.Current.CancellationToken);

    [Theory]
    [MemberData(nameof(ProbeVectors.Cases), MemberType = typeof(ProbeVectors))]
    public async Task TheDownload_IsTimedAsTheServersCasesSay(string name, ProbeCase probe)
    {
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Body(TimedProbeStream.Of(_time, probe)));

        var reading = await MeasureAsync(probe.Mode, probe.TargetKbps);

        Assert.Equal((name, probe.Expect), (name, reading.Result));
        Assert.Equal((name, probe.Expect is null ? ProbeReadingKind.TooLittleData : ProbeReadingKind.Measured), (name, reading.Kind));
    }

    [Fact]
    public async Task TheProbe_IsAskedForItsBytesWithTheAppsCredentials()
    {
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Body(TimedProbeStream.Steady(_time, 400_000, 11_000)));

        var reading = await MeasureAsync(SpeedTestMode.Accurate);

        Assert.Equal(new SpeedTestResult(400_000, false, 150_000_000, 3000), reading.Result);
        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(SiloAuth.Profile, sent.Auth);
        Assert.Equal("?size=1000000000", sent.Uri.Query);
        var request = Assert.Single(_silo.Handler.Requests);
        Assert.Equal("application/octet-stream", Assert.Single(request.Headers.Accept).MediaType);
        Assert.True(request.Headers.CacheControl?.NoStore);
        Assert.Empty(_clients.Names);
    }

    [Fact]
    public async Task ARedirect_IsFollowedOnce_WithNoneOfTheAppsCredentials()
    {
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Redirect("https://edge.example/probe?token=t0k3n"));
        _hopAnswer = ProbeAnswers.Body(TimedProbeStream.Steady(_time, 80_000, 11_000));

        var reading = await MeasureAsync(SpeedTestMode.Accurate);

        Assert.Equal(ProbeReadingKind.Measured, reading.Kind);
        Assert.Equal(80_000, reading.Result!.Kbps);
        Assert.Equal([BandwidthProbeApi.HopClientName], _clients.Names);
        var hop = Assert.Single(_hop.Requests);
        Assert.Equal(new Uri("https://edge.example/probe?token=t0k3n"), hop.RequestUri);
        Assert.Null(hop.Headers.Authorization);
        Assert.DoesNotContain(hop.Headers, header => header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SiloAuth.Anonymous, hop.GetAuth());
        Assert.Null(hop.GetServer());
        Assert.Equal("application/octet-stream", Assert.Single(hop.Headers.Accept).MediaType);
    }

    [Fact]
    public async Task ARelativeRedirect_IsResolvedAgainstTheProbe()
    {
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Redirect("/silo/probe-hop?t=1", HttpStatusCode.Found));
        _hopAnswer = ProbeAnswers.Body(TimedProbeStream.Steady(_time, 80_000, 11_000));

        await MeasureAsync(SpeedTestMode.Accurate);

        Assert.Equal(new Uri("https://nas.example/silo/probe-hop?t=1"), Assert.Single(_hop.Requests).RequestUri);
    }

    [Fact]
    public async Task ASecondRedirect_IsNotFollowed()
    {
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Redirect("https://edge.example/probe"));
        _hopAnswer = ProbeAnswers.Redirect("https://other.example/probe");

        var reading = await MeasureAsync(SpeedTestMode.Accurate);

        Assert.Equal(ProbeReadingKind.Unavailable, reading.Kind);
        Assert.Single(_hop.Requests);
    }

    [Theory]
    [InlineData(12.0, 12)]
    [InlineData(0.2, 1)]
    [InlineData(null, 30)]
    public async Task ARateLimit_SaysWhenToTryAgain(double? retryAfterSeconds, int expected)
    {
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.RateLimited(retryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null));

        var reading = await MeasureAsync(SpeedTestMode.Accurate);

        Assert.Equal(new ProbeReading(ProbeReadingKind.RateLimited, RetryAfterSeconds: expected), reading);
    }

    [Fact]
    public async Task AnotherAnswer_MeasuresNothing()
    {
        _silo.On(HttpMethod.Get, ProbePath, ScriptedSilo.Problem(503, "unavailable"));

        var reading = await MeasureAsync(SpeedTestMode.Accurate);

        Assert.Equal(ProbeReadingKind.Unavailable, reading.Kind);
    }

    [Fact]
    public async Task NoFirstByteWithinTenSeconds_IsAbandoned()
    {
        var body = new TimedProbeStream(_time) { FirstByteAfter = TimeSpan.FromSeconds(11) }.Chunk(0, 1_000_000);
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Body(body));

        var reading = await MeasureAsync(SpeedTestMode.Accurate);

        Assert.Equal(ProbeReadingKind.NetworkError, reading.Kind);
        Assert.Equal(TimeSpan.FromSeconds(11), _time.Elapsed);
    }

    [Fact]
    public async Task AFailedRequest_IsANetworkError()
    {
        _silo.On(HttpMethod.Get, ProbePath, ScriptedSilo.NetworkError());

        var reading = await MeasureAsync(SpeedTestMode.Accurate);

        Assert.Equal(ProbeReadingKind.NetworkError, reading.Kind);
    }

    [Fact]
    public async Task ATestThatRunsOn_IsClosedAtItsLimit()
    {
        var body = TimedProbeStream.Steady(_time, 200_000, 60_000);
        _silo.On(HttpMethod.Get, ProbePath, ProbeAnswers.Body(body));

        var reading = await MeasureAsync(SpeedTestMode.Accurate);

        Assert.Equal(200_000, reading.Result!.Kbps);
        // Read up to the test's 10 s, not the minute the body would run.
        Assert.True(_time.Elapsed <= TimeSpan.FromSeconds(10.1), $"The read ran {_time.Elapsed}");
    }
}
