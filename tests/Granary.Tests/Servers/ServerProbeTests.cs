using System.Net;
using Granary.Core.Servers;
using Granary.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Granary.Tests.Servers;

public sealed class ServerProbeTests
{
    private static readonly Uri Base = new("https://nas.example/silo");

    private static ServerProbe Probe(FakeHttpMessageHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler), NullLogger<ServerProbe>.Instance, timeout ?? ServerProbe.DefaultTimeout);

    [Fact]
    public async Task ProbeAsync_ReturnsCompatible_ForAV2Server()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"api_major":2,"contract_digest":"d","server_version":"abc123","links":{"capabilities":"/api/v2/capabilities","identity":"/api/v2/system/identity","openapi":"/api/v2/openapi.json"}}""");

        var result = await Probe(handler).ProbeAsync(Base, TestContext.Current.CancellationToken);

        var compatible = Assert.IsType<ServerProbeResult.Compatible>(result);
        Assert.Equal(2, compatible.Info.ApiMajor);
        Assert.Equal("/api/v2/system/identity", compatible.Info.Links?.Identity);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://nas.example/silo/api/v2/system/info", request.RequestUri!.AbsoluteUri);
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");
        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData("404 page not found")]
    [InlineData("404 page not found\n")]
    public async Task ProbeAsync_ReturnsUpdateRequired_ForGoNotFoundBody(string body)
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.NotFound, body, "text/plain");

        var result = await Probe(handler).ProbeAsync(Base, TestContext.Current.CancellationToken);

        Assert.IsType<ServerProbeResult.UpdateRequired>(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "<html>not here</html>")]
    [InlineData(HttpStatusCode.NotFound, "404 page not found\n\n")]
    [InlineData(HttpStatusCode.OK, """{"api_major":3}""")]
    [InlineData(HttpStatusCode.OK, "<!doctype html><title>Some other app</title>")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    public async Task ProbeAsync_ReturnsNoVerdict_ForAnythingElse(HttpStatusCode status, string body)
    {
        var handler = FakeHttpMessageHandler.Returning(status, body, "text/html");

        var result = await Probe(handler).ProbeAsync(Base, TestContext.Current.CancellationToken);

        Assert.IsType<ServerProbeResult.NoVerdict>(result);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsNoVerdict_OnTimeout()
    {
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await Probe(handler, TimeSpan.FromMilliseconds(50)).ProbeAsync(Base, TestContext.Current.CancellationToken);

        var noVerdict = Assert.IsType<ServerProbeResult.NoVerdict>(result);
        Assert.Equal("timeout", noVerdict.Reason);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsNoVerdict_OnNetworkError()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"));

        var result = await Probe(handler).ProbeAsync(Base, TestContext.Current.CancellationToken);

        var noVerdict = Assert.IsType<ServerProbeResult.NoVerdict>(result);
        Assert.StartsWith("network", noVerdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_PropagatesCallerCancellation()
    {
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe(handler).ProbeAsync(Base, cts.Token));
    }
}
