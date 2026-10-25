using System.Net;
using System.Text;
using Granary.Core.Api;
using Granary.Core.Http;
using Granary.Core.Servers;
using Granary.Tests.Fakes;

namespace Granary.Tests.Servers;

/// <summary>The route table and the handler that sends a saved server's requests over its local route.</summary>
public sealed class LocalRouteTests : IDisposable
{
    private static readonly Uri Saved = new("https://silo.example/");
    private static readonly Uri Lan = new("https://192.168.1.20:8080/");

    private readonly LocalRoutes _routes = new();
    private readonly LogSink _logs = new();
    private readonly List<HttpRequestMessage> _local = [];

    // As sent: the handler puts the saved address back on the request afterwards.
    private readonly List<string> _localUris = [];
    private readonly List<HttpRequestMessage> _saved = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _lan = _ => Ok("lan");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void ARewrite_KeepsThePathAndQueryAsWritten()
    {
        using var route = Route();

        var local = route.Rewrite(new Uri("https://silo.example/api/v2/stream/s1?seek=600&x=%41", new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));

        Assert.Equal("https://192.168.1.20:8080/api/v2/stream/s1?seek=600&x=%41", local.OriginalString);
        Assert.Equal("/api/v2/stream/s1?seek=600&x=%41", local.PathAndQuery);
    }

    [Fact]
    public void ARoute_TakesItsSavedOriginOnly()
    {
        _routes.Use(Route());

        Assert.NotNull(_routes.Find(new Uri("https://silo.example/api/v2/catalog")));
        Assert.Null(_routes.Find(new Uri("https://silo.example:8443/api/v2/catalog")));
        Assert.Null(_routes.Find(new Uri("https://node.example/stream/v3/s1")));
        Assert.Null(_routes.Find(new Uri("http://silo.example/api/v2/catalog")));
    }

    [Fact]
    public void ANewRoute_ReplacesTheOldOne_AndDropOnlyDropsItsOwn()
    {
        var first = Route();
        var second = Route();
        _routes.Use(first);
        _routes.Use(second);

        _routes.Drop(first);

        Assert.Same(second, _routes.Find(Saved));
    }

    [Fact]
    public async Task ARequestForTheSavedAddress_GoesOverTheRoute_WithItsHeaders()
    {
        _routes.Use(Route());
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://silo.example/api/v2/catalog/home");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer token");

        using var response = await Send(request);

        Assert.Equal("lan", await response.Content.ReadAsStringAsync(Cancellation));
        var sent = Assert.Single(_local);
        Assert.Equal("https://192.168.1.20:8080/api/v2/catalog/home", Assert.Single(_localUris));
        Assert.Equal("Bearer token", sent.Headers.Authorization!.ToString());
        Assert.Empty(_saved);
        // A replay (after a token refresh) starts from the saved address again.
        Assert.Equal("https://silo.example/api/v2/catalog/home", request.RequestUri!.OriginalString);
    }

    [Fact]
    public async Task OtherOrigins_AndTheSavedAddressOnlyRequests_AreLeftAlone()
    {
        _routes.Use(Route());
        using var node = new HttpRequestMessage(HttpMethod.Get, "https://node.example/stream/v3/s1");
        using var connections = new HttpRequestMessage(HttpMethod.Get, "https://silo.example/api/v2/system/connections");
        connections.Options.Set(SiloRequestOptions.SavedAddressOnly, true);

        (await Send(node)).Dispose();
        (await Send(connections)).Dispose();

        Assert.Empty(_local);
        Assert.Equal(2, _saved.Count);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public async Task ARouteThatDoesNotConnect_IsDropped_AndTheSavedAddressAnswers(HttpRequestError error)
    {
        // SecureConnectionError: the key is not the pinned one (rotated, or another machine at that address).
        _lan = _ => throw new HttpRequestException(error, "unreachable");
        _routes.Use(Route());
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://silo.example/api/v2/stream/s1");

        using var response = await Send(request);

        Assert.Equal("saved", await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal("https://silo.example/api/v2/stream/s1", Assert.Single(_saved).RequestUri!.OriginalString);
        Assert.Null(_routes.Find(Saved));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Writes_TakeTheRoute(string method)
    {
        // The server keys what it knows of a device's connection (where a start plays from, a speed test's result) by
        // the path a request came over: writes must come the way the media goes, as Android sends them.
        _routes.Use(Route());
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://silo.example/api/v2/playback/start")
        {
            Content = new StringContent("""{"file_id":"1"}""", Encoding.UTF8, "application/json"),
        };

        (await Send(request)).Dispose();

        Assert.Equal("https://192.168.1.20:8080/api/v2/playback/start", Assert.Single(_localUris));
        Assert.Empty(_saved);
    }

    [Theory]
    [InlineData("POST", "/api/v2/auth/login")]
    [InlineData("POST", "/api/v2/auth/refresh")]
    [InlineData("GET", "/api/v2/auth/providers")]
    public async Task SignIn_GoesToTheSavedAddress(string method, string path)
    {
        _routes.Use(Route());
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://silo.example" + path);

        (await Send(request)).Dispose();

        Assert.Empty(_local);
        Assert.Single(_saved);
    }

    [Fact]
    public async Task AWriteWhoseRouteDidNotConnect_GoesToTheSavedAddress_WithItsBody()
    {
        // No connection was made: the server cannot have it, so sending it again does it once.
        _lan = _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable");
        _routes.Use(Route());
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://silo.example/api/v2/playback/s1/progress")
        {
            Content = new StringContent("""{"sequence":1}""", Encoding.UTF8, "application/json"),
        };

        using var response = await Send(request);

        Assert.Equal("saved", await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal("""{"sequence":1}""", await Assert.Single(_saved).Content!.ReadAsStringAsync(Cancellation));
    }

    [Fact]
    public async Task AWriteThatFailedAfterConnecting_IsNotRepeated()
    {
        // It may have reached the server: a second one could start a session twice.
        _lan = _ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "ended");
        _routes.Use(Route());
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://silo.example/api/v2/playback/start")
        {
            Content = new StringContent("""{"file_id":"1"}""", Encoding.UTF8, "application/json"),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => Send(request));

        Assert.Empty(_saved);
    }

    [Fact]
    public void ARewrite_OfAQueryWithoutAPath_KeepsTheQuery()
    {
        using var route = Route();

        Assert.Equal("https://192.168.1.20:8080/?x=/a", route.Rewrite(new Uri("https://silo.example?x=/a")).OriginalString);
    }

    [Fact]
    public async Task AFailureAfterConnecting_IsNotRepeated()
    {
        // The request may have reached the server: sending it again could apply it twice.
        _lan = _ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "ended");
        _routes.Use(Route());
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://silo.example/api/v2/catalog/home");

        await Assert.ThrowsAsync<HttpRequestException>(() => Send(request));

        Assert.Empty(_saved);
    }

    public void Dispose() => _routes.Dispose();

    private LocalRoute Route() => new(Saved, Lan, "pin", new HttpMessageInvoker(new FakeHttpMessageHandler((request, _) =>
    {
        _local.Add(request);
        _localUris.Add(request.RequestUri!.OriginalString);
        return Task.FromResult(_lan(request));
    })));

    private Task<HttpResponseMessage> Send(HttpRequestMessage request)
    {
        var handler = new LocalRouteHandler(_routes, _logs.For<LocalRouteHandler>())
        {
            InnerHandler = new FakeHttpMessageHandler((sent, _) =>
            {
                _saved.Add(sent);
                return Task.FromResult(Ok("saved"));
            }),
        };
        return new HttpMessageInvoker(handler).SendAsync(request, Cancellation);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}
