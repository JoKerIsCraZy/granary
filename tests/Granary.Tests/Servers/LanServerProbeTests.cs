using System.Net;
using System.Text;
using Granary.Core.Servers;
using Granary.Tests.Fakes;

namespace Granary.Tests.Servers;

/// <summary>Reading an address that only advertised itself: plainly, briefly, and without following it anywhere.</summary>
public sealed class LanServerProbeTests
{
    private static readonly Uri Origin = new("http://192.168.1.6:8090/");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheIdentityAndName_AreReadWithoutAnythingOfThisDevice()
    {
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/system/identity" => """{ "server_id": " srv-1 " }""",
            _ => """{ "server_name": "Living room", "login_subtitle": null }""",
        })));
        var probe = new LanServerProbe(new HttpClient(handler));

        Assert.Equal("srv-1", await probe.GetServerIdAsync(Origin, Cancellation));
        Assert.Equal("Living room", await probe.GetNameAsync(Origin, Cancellation));

        Assert.Equal(["http://192.168.1.6:8090/api/v2/system/identity", "http://192.168.1.6:8090/api/v2/theme/branding"],
            handler.Requests.Select(r => r.RequestUri!.AbsoluteUri));
        Assert.All(handler.Requests, r =>
        {
            Assert.Null(r.Headers.Authorization);
            Assert.DoesNotContain(r.Headers, h => h.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task ARedirect_IsNoAnswer()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://127.0.0.1:8090/api/v2/system/identity");
            return Task.FromResult(response);
        });

        Assert.Null(await new LanServerProbe(new HttpClient(handler)).GetServerIdAsync(Origin, Cancellation));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void TheProbesHandler_FollowsNoRedirect_AndUsesNoProxyOrCookies()
    {
        using var handler = LanServerProbe.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
    }

    [Theory]
    [InlineData("""{ "server_id": "" }""")]
    [InlineData("""{ "server_id": 7 }""")]
    [InlineData("""{ "server_id": "a b" }""")]
    [InlineData("""[ "server_id" ]""")]
    [InlineData("not json")]
    public async Task AnUnusableAnswer_IsNone(string body)
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(Json(body)));

        Assert.Null(await new LanServerProbe(new HttpClient(handler)).GetServerIdAsync(Origin, Cancellation));
    }

    [Fact]
    public async Task AnAnswerPast64KB_IsNotRead()
    {
        string padding = new('x', LanServerProbe.MaxBodyBytes);
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Streamed, so no length tells it in advance.
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes($$"""{ "server_id": "srv-1", "pad": "{{padding}}" }"""))),
        }));

        Assert.Null(await new LanServerProbe(new HttpClient(handler)).GetServerIdAsync(Origin, Cancellation));
    }

    [Fact]
    public async Task AnErrorOrAFailure_IsNoAnswer()
    {
        var notFound = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var refused = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        Assert.Null(await new LanServerProbe(new HttpClient(notFound)).GetServerIdAsync(Origin, Cancellation));
        Assert.Null(await new LanServerProbe(new HttpClient(refused)).GetNameAsync(Origin, Cancellation));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
