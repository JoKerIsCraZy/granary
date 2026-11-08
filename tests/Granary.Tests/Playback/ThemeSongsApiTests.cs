using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Granary.Core;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Playback;
using Granary.Core.Playback.Themes;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;
using Microsoft.Extensions.DependencyInjection;

namespace Granary.Tests.Playback;

/// <summary>The local theme songs API and the theme's download (docs/api-contract.md 7.19).</summary>
public sealed class ThemeSongsApiTests
{
    internal const string CapabilityPath = "api/v2/catalog/themes/capabilities";

    internal static string PlaybackPath(string owner, string theme) => $"api/v2/catalog/items/{owner}/themes/{theme}/playback";

    internal const string Available = """
        { "state": "available", "allowed": true, "delivery": "routed", "transcode": true, "cluster_routing": false,
          "grant_lifetime_seconds": 300, "revision": "r1" }
        """;

    internal static string Grant(string url = "/api/v2/catalog/items/movie-1/themes/11/audio?token=tkn-secret", string delivery = "original",
        string contentType = "audio/mpeg") => $$"""
        { "url": "{{url}}", "expires_at": "2026-11-08T12:05:00Z", "delivery": "{{delivery}}", "content_type": "{{contentType}}" }
        """;

    private readonly ScriptedSilo _silo = new();
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Available, true)]
    [InlineData("""{ "state": "available", "allowed": false, "revision": "r" }""", false)]
    [InlineData("""{ "state": "available", "revision": "r" }""", true)]
    [InlineData("""{ "state": "disabled", "allowed": true, "revision": "r" }""", false)]
    [InlineData("""{ "state": "not_configured", "revision": "r" }""", false)]
    public async Task TheCapability_SaysWhetherThemesPlay(string answer, bool available)
    {
        _silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, answer));

        var capability = await new ThemesApi(_silo.Http).GetCapabilityAsync(ScriptedSilo.Base, _token);

        Assert.Equal(available, capability.IsAvailable);
        Assert.Equal(SiloAuth.Profile, Assert.Single(_silo.Sent).Auth);
    }

    [Theory]
    [InlineData(Available, true, false)]
    [InlineData("""{ "state": "available", "delivery": "local_direct_play", "cluster_routing": false, "revision": "r" }""", false, false)]
    [InlineData("""{ "state": "available", "delivery": "routed", "cluster_routing": true, "revision": "r" }""", true, true)]
    [InlineData("""{ "state": "available", "revision": "r" }""", false, false)]
    public async Task TheCapability_SaysWhetherThemesMayComeFromAProxyNode(string answer, bool routed, bool clusterRouting)
    {
        _silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, answer));

        var capability = await new ThemesApi(_silo.Http).GetCapabilityAsync(ScriptedSilo.Base, _token);

        Assert.Equal(routed, capability.IsRouted);
        Assert.Equal(clusterRouting, capability.ClusterRouting);
    }

    [Fact]
    public async Task AGrant_SaysWhatThisPCPlays_ForTheProfile()
    {
        _silo.On(HttpMethod.Post, PlaybackPath("movie-1", "11"), ScriptedSilo.Json(HttpStatusCode.OK, Grant()));

        var grant = await new ThemesApi(_silo.Http).CreatePlaybackAsync(ScriptedSilo.Base, "movie-1", "11",
            [new ThemeAudioFormat("mp3", "mp3"), new ThemeAudioFormat("mp4", "aac")], _token);

        var sent = Assert.Single(_silo.Sent);
        Assert.Equal(SiloAuth.Profile, sent.Auth);
        Assert.Equal("""{"accepted_formats":[{"container":"mp3","audio_codec":"mp3"},{"container":"mp4","audio_codec":"aac"}]}""", sent.Body);
        Assert.False(grant.IsConverted);
        Assert.Equal("audio/mpeg", grant.ContentType);
    }

    [Fact]
    public async Task WithoutFormats_TheGrantAsksForTheOriginal_WithNoBody()
    {
        _silo.On(HttpMethod.Post, PlaybackPath("movie-1", "11"), ScriptedSilo.Json(HttpStatusCode.OK, Grant(delivery: "converted")));

        var grant = await new ThemesApi(_silo.Http).CreatePlaybackAsync(ScriptedSilo.Base, "movie-1", "11", [], _token);

        Assert.Null(Assert.Single(_silo.Sent).Body);
        Assert.True(grant.IsConverted);
    }

    [Fact]
    public void AGrant_NeverShowsItsAddress()
    {
        var grant = JsonSerializer.Deserialize(Grant(), ThemesJsonContext.Default.ThemePlayback)!;

        Assert.DoesNotContain("tkn-secret", grant.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/audio", grant.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIdThatIsNoId_IsNotSent()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ThemesApi(_silo.Http).CreatePlaybackAsync(ScriptedSilo.Base, "..", "11", [], _token));
        Assert.Empty(_silo.Sent);
    }

    private static ThemePlayback Parsed(string json) => JsonSerializer.Deserialize(json, ThemesJsonContext.Default.ThemePlayback)!;

    private static (ThemeAudioDownloader Downloader, FakeHttpMessageHandler Handler) Downloader(HttpStatusCode status = HttpStatusCode.OK,
        byte[]? body = null, long? length = null)
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var content = new ByteArrayContent(body ?? [7, 8, 9]);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            if (length is { } declared)
            {
                content.Headers.ContentLength = declared;
            }
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        });
        return (new ThemeAudioDownloader(new FakeHttpClients(handler)), handler);
    }

    [Fact]
    public async Task TheTheme_IsFetchedWhole_BeneathTheServer_WithoutCredentials()
    {
        var (downloader, handler) = Downloader();

        var audio = await downloader.DownloadAsync(ScriptedSilo.Base, Parsed(Grant()), ThemeOrigins.Server, _token);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://nas.example/silo/api/v2/catalog/items/movie-1/themes/11/audio?token=tkn-secret", request.RequestUri!.OriginalString);
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("X-Profile-Id"));
        Assert.Equal(new byte[] { 7, 8, 9 }, audio.Audio.ToArray());
        Assert.Equal("audio/mpeg", audio.ContentType);
        Assert.False(audio.Converted);
    }

    [Fact]
    public async Task AConversion_IsMarkedSo()
    {
        var (downloader, _) = Downloader();

        var audio = await downloader.DownloadAsync(ScriptedSilo.Base, Parsed(Grant(delivery: "converted", contentType: "audio/mp4")), ThemeOrigins.Server, _token);

        Assert.True(audio.Converted);
        Assert.Equal("audio/mp4", audio.ContentType);
    }

    [Fact]
    public async Task AProxyNode_IsFetchedOnItsOwnOrigin()
    {
        var (downloader, handler) = Downloader();

        await downloader.DownloadAsync(ScriptedSilo.Base, Parsed(Grant("https://proxy.example:8443/theme-audio/abc?token=t")), ThemeOrigins.Routed, _token);

        Assert.Equal("https://proxy.example:8443/theme-audio/abc?token=t", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task WhileTheServerServesThemesItself_AnotherOrigin_IsRefused_Unsent()
    {
        var (downloader, handler) = Downloader();

        await Assert.ThrowsAsync<ThemeRefusedException>(() =>
            downloader.DownloadAsync(ScriptedSilo.Base, Parsed(Grant("https://proxy.example:8443/theme-audio/abc?token=t")), ThemeOrigins.Server, _token));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://nas.example/silo", "https://proxy.example/theme-audio/abc", true)]
    [InlineData("https://nas.example/silo", "https://proxy.example/theme-audio/abc", true)]
    [InlineData("http://nas.example/silo", "http://proxy.example/theme-audio/abc", true)]
    [InlineData("https://nas.example/silo", "http://proxy.example/theme-audio/abc", false)]
    public void ANodesScheme_IsTheServers_OrHttpsBesideAPlainHttpServer(string server, string url, bool fetched)
    {
        foreach (var origins in new[] { ThemeOrigins.Routed, ThemeOrigins.Authorized })
        {
            Assert.Equal(fetched, PlaybackUrl.ResolveTheme(new Uri(server), url, origins) is not null);
        }
    }

    [Theory]
    // A proxy node beside a server on the same LAN, as a self-hosted server mostly runs.
    [InlineData("http://192.168.1.10:8095", "http://192.168.1.20:8096/stream/theme/abc", true)]
    [InlineData("https://10.0.0.2/silo", "https://10.0.0.3/stream/theme/abc", true)]
    [InlineData("http://[fd00::10]:8095", "http://[fd00::20]:8096/stream/theme/abc", true)]
    [InlineData("http://nas.local:8095", "http://192.168.1.20:8096/stream/theme/abc", true)]
    [InlineData("http://nas:8095", "http://192.168.1.20:8096/stream/theme/abc", true)]
    [InlineData("http://192.168.1.10:8095", "http://[::ffff:192.168.1.20]:8096/stream/theme/abc", true)]
    // A server on the internet that does not vouch for its proxies: a private address is refused.
    [InlineData("https://silo.example", "https://192.168.1.20/stream/theme/abc", false)]
    [InlineData("https://silo.example", "https://[fd00::20]/stream/theme/abc", false)]
    [InlineData("https://203.0.113.5", "https://10.0.0.3/stream/theme/abc", false)]
    // A public node is fetched from either.
    [InlineData("https://silo.example", "https://proxy.example/stream/theme/abc", true)]
    [InlineData("http://192.168.1.10:8095", "http://proxy.example/stream/theme/abc", true)]
    public void WithoutAuthorizedOrigins_APrivateNodeIsFetched_OnlyForAServerOnAPrivateNetwork(string server, string url, bool fetched)
    {
        Assert.Equal(fetched, PlaybackUrl.ResolveTheme(new Uri(server), url, ThemeOrigins.Routed) is not null);
    }

    [Theory]
    [InlineData("https://192.168.1.20/stream/theme/abc")]
    [InlineData("https://[fd00::20]/stream/theme/abc")]
    [InlineData("https://proxy.example/stream/theme/abc")]
    public void AnOriginTheServerAuthorizes_IsFetched_PrivateOrNot(string url)
    {
        Assert.NotNull(PlaybackUrl.ResolveTheme(new Uri("https://silo.example"), url, ThemeOrigins.Authorized));
    }

    [Theory]
    [InlineData("https://[::ffff:169.254.169.254]/latest")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("https://[fe80::1]/stream/theme/abc")]
    [InlineData("https://127.0.0.1/stream/theme/abc")]
    [InlineData("https://[::ffff:127.0.0.1]/stream/theme/abc")]
    [InlineData("https://[::1]/stream/theme/abc")]
    [InlineData("https://localhost/stream/theme/abc")]
    [InlineData("https://0.0.0.0/stream/theme/abc")]
    [InlineData("https://[::]/stream/theme/abc")]
    [InlineData("https://239.1.2.3/stream/theme/abc")]
    [InlineData("https://[ff02::1]/stream/theme/abc")]
    [InlineData("https://user:pass@proxy.example/stream/theme/abc")]
    [InlineData("https://proxy.example/stream/theme/abc#part")]
    public void ThisPC_LinkLocal_Multicast_UnspecifiedAndUserInfo_AreNeverFetched(string url)
    {
        foreach (string server in new[] { "https://silo.example", "https://192.168.1.10" })
        {
            Assert.Null(PlaybackUrl.ResolveTheme(new Uri(server), url, ThemeOrigins.Authorized));
            Assert.Null(PlaybackUrl.ResolveTheme(new Uri(server), url, ThemeOrigins.Routed));
        }
    }

    [Theory]
    [InlineData("http://localhost./stream/theme/abc")]
    [InlineData("http://LOCALHOST./stream/theme/abc")]
    [InlineData("http://foo.localhost/stream/theme/abc")]
    [InlineData("http://foo.localhost./stream/theme/abc")]
    [InlineData("http://127.0.0.1./stream/theme/abc")]
    [InlineData("http://127.1.2.3./stream/theme/abc")]
    [InlineData("http://0.0.0.0./stream/theme/abc")]
    [InlineData("http://169.254.169.254./latest")]
    [InlineData("http://2130706433./stream/theme/abc")]
    [InlineData("http://[2002:7f00:1::]/stream/theme/abc")]
    [InlineData("http://[2002:a9fe:a9fe::]/latest")]
    [InlineData("http://[64:ff9b::7f00:1]/stream/theme/abc")]
    [InlineData("http://[::7f00:1]/stream/theme/abc")]
    public void ThisPC_SpelledAnotherWay_IsNeverFetched(string url)
    {
        foreach (string server in new[] { "http://silo.example", "http://192.168.1.10:8095" })
        {
            Assert.Null(PlaybackUrl.ResolveTheme(new Uri(server), url, ThemeOrigins.Authorized));
            Assert.Null(PlaybackUrl.ResolveTheme(new Uri(server), url, ThemeOrigins.Routed));
        }
    }

    [Theory]
    [InlineData("http://192.168.1.20./stream/theme/abc")]
    [InlineData("http://[2002:c0a8:114::]/stream/theme/abc")]
    [InlineData("http://[64:ff9b::c0a8:114]/stream/theme/abc")]
    public void APrivateNodeSpelledAnotherWay_IsStillPrivate(string url)
    {
        Assert.Null(PlaybackUrl.ResolveTheme(new Uri("http://silo.example"), url, ThemeOrigins.Routed));
        Assert.NotNull(PlaybackUrl.ResolveTheme(new Uri("http://192.168.1.10:8095"), url, ThemeOrigins.Routed));
    }

    [Fact]
    public void WithoutRouting_OnlyTheServersOwnOriginIsFetched()
    {
        var server = new Uri("http://192.168.1.10:8095");

        Assert.Null(PlaybackUrl.ResolveTheme(server, "http://192.168.1.20:8096/stream/theme/abc", ThemeOrigins.Server));
        Assert.NotNull(PlaybackUrl.ResolveTheme(server, "/api/v2/catalog/items/movie-1/themes/11/audio?token=t", ThemeOrigins.Server));
    }

    [Theory]
    [InlineData("http://proxy.example/theme-audio/abc?token=t")]
    [InlineData("https://user:pass@proxy.example/theme-audio/abc")]
    [InlineData("https://127.0.0.1/theme-audio/abc")]
    [InlineData("https://localhost/theme-audio/abc")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("https://[::ffff:169.254.169.254]/latest")]
    [InlineData("https://[::ffff:127.0.0.1]/theme-audio/abc")]
    [InlineData("https://[::1]/theme-audio/abc")]
    [InlineData("https://10.0.0.5/theme-audio/abc")]
    [InlineData("https://172.20.1.2/theme-audio/abc")]
    [InlineData("https://192.168.1.20/theme-audio/abc")]
    [InlineData("https://[::ffff:192.168.1.20]/theme-audio/abc")]
    [InlineData("https://[fd12:3456::1]/theme-audio/abc")]
    [InlineData("https://[fe80::1]/theme-audio/abc")]
    [InlineData("/api/v2/catalog/items/movie-1/themes/11/../../../../admin")]
    [InlineData("ftp://proxy.example/theme")]
    [InlineData("")]
    public async Task AnAddressGranaryDoesNotFetch_IsRefused_Unsent(string url)
    {
        var (downloader, handler) = Downloader();

        await Assert.ThrowsAsync<ThemeRefusedException>(() => downloader.DownloadAsync(ScriptedSilo.Base, Parsed(Grant(url)), ThemeOrigins.Routed, _token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void TheThemeClient_KeepsNoCookies_FollowsNoRedirect_AndTakesTheBodyAsSent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGranaryCore();
        using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ThemeAudioDownloader.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.UseCookies);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, sockets.AutomaticDecompression);
        Assert.NotNull(sockets.ConnectCallback);
    }

    // The theme client's own handler over a resolver the test answers, so a node's connection is checked without the network.
    private static (ThemeAudioDownloader Downloader, List<string> Resolved) ResolvingTo(params string[] addresses)
    {
        var resolved = new List<string>();
        var handler = ThemeAudioDownloader.CreateHandler((host, _) =>
        {
            resolved.Add(host);
            return Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());
        });
        return (new ThemeAudioDownloader(new FakeHttpClients(handler)), resolved);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.9.10")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.1.1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("::7f00:1")]
    [InlineData("fe80::1")]
    [InlineData("224.0.0.1")]
    public async Task ANodeNameThatResolvesToThisPC_IsRefusedAtConnect_AuthorizedOrNot(string address)
    {
        foreach (var origins in new[] { ThemeOrigins.Routed, ThemeOrigins.Authorized })
        {
            var (downloader, resolved) = ResolvingTo(address);

            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(new Uri("http://silo.example"),
                Parsed(Grant("http://node.example:8096/stream/theme/abc")), origins, _token));

            Assert.Equal(["node.example"], resolved);
            Assert.True(RefusedAtConnect(failure));
        }
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.1.2.3")]
    [InlineData("fd00::20")]
    [InlineData("::ffff:192.168.1.20")]
    public async Task ANodeNameThatResolvesToAPrivateAddress_IsRefusedAtConnect_ForAServerOnTheInternet(string address)
    {
        var (downloader, resolved) = ResolvingTo(address);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(new Uri("http://silo.example"),
            Parsed(Grant("http://node.example:8096/stream/theme/abc")), ThemeOrigins.Routed, _token));

        Assert.Single(resolved);
        Assert.True(RefusedAtConnect(failure));
    }

    // Refused by the node's rule, not by an attempt to connect that failed.
    private static bool RefusedAtConnect(Exception failure)
    {
        for (Exception? at = failure; at is not null; at = at.InnerException)
        {
            if (at.Message.Contains("address Granary does not fetch", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    [Theory]
    [InlineData("http://192.168.1.10:8095", ThemeOrigins.Routed, "192.168.1.20", true)]
    [InlineData("http://nas.local:8095", ThemeOrigins.Routed, "192.168.1.20", true)]
    [InlineData("http://silo.example", ThemeOrigins.Routed, "192.168.1.20", false)]
    [InlineData("http://silo.example", ThemeOrigins.Authorized, "192.168.1.20", true)]
    [InlineData("http://silo.example", ThemeOrigins.Routed, "203.0.113.7", true)]
    [InlineData("http://192.168.1.10:8095", ThemeOrigins.Authorized, "127.0.0.1", false)]
    public void ANodesConnection_TakesWhatItsServerVouchesFor(string server, ThemeOrigins origins, string address, bool taken)
    {
        var resolved = PlaybackUrl.ResolveTheme(new Uri(server), "http://node.example:8096/stream/theme/abc", origins);

        var node = Assert.IsType<ThemeNode>(resolved?.Node);
        Assert.Equal(taken, node.Takes(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task EveryThemeDownload_OpensAConnectionOfItsOwn_SoNoRuleOutlivesItsRequest()
    {
        await using var silo = new KeepAliveServer([7, 8, 9]);
        var server = new Uri($"http://127.0.0.1:{silo.Port}/");
        var (downloader, _) = ResolvingTo("203.0.113.7");

        await downloader.DownloadAsync(server, Parsed(Grant()), ThemeOrigins.Authorized, _token);
        await downloader.DownloadAsync(server, Parsed(Grant()), ThemeOrigins.Authorized, _token);

        Assert.Equal(2, silo.Requests);
        Assert.Equal(2, silo.Connections);
    }

    // A loopback HTTP/1.1 server that keeps each connection open for the next request, and counts both.
    private sealed class KeepAliveServer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _body;
        private readonly Task _loop;
        private int _connections;
        private int _requests;

        public KeepAliveServer(byte[] body)
        {
            _body = body;
            _listener.Start();
            _loop = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Connections => Volatile.Read(ref _connections);

        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
                {
                    return;
                }
                Interlocked.Increment(ref _connections);
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(System.Net.Sockets.TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var head = new System.Text.StringBuilder();
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        int read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                        {
                            return;
                        }
                        head.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                        while (head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) is var end and >= 0)
                        {
                            head.Remove(0, end + 4);
                            Interlocked.Increment(ref _requests);
                            byte[] answer = System.Text.Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nContent-Length: {_body.Length}\r\n\r\n");
                            await stream.WriteAsync(answer, _stop.Token);
                            await stream.WriteAsync(_body, _stop.Token);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Net.Sockets.SocketException)
                {
                    // The client hung up.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task AServerOnThisPC_StillFetchesItsOwnTheme_AsItResolves()
    {
        await using var silo = new TinyHttpServer(200, "OK", [7, 8, 9], "audio/mpeg");
        var server = new Uri($"http://localhost:{silo.Url("").Port}/");
        var (downloader, resolved) = ResolvingTo("203.0.113.7");

        var audio = await downloader.DownloadAsync(server, Parsed(Grant()), ThemeOrigins.Authorized, _token);

        Assert.Equal(new byte[] { 7, 8, 9 }, audio.Audio.ToArray());
        Assert.Empty(resolved);
        var own = PlaybackUrl.ResolveTheme(server, "/api/v2/catalog/items/movie-1/themes/11/audio?token=t", ThemeOrigins.Authorized);
        Assert.NotNull(own);
        Assert.Null(own.Node);
    }

    [Fact]
    public async Task ARefusedDownload_Fails()
    {
        var (downloader, _) = Downloader(HttpStatusCode.Forbidden);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(ScriptedSilo.Base, Parsed(Grant()), ThemeOrigins.Server, _token));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        Assert.DoesNotContain("tkn-secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SomethingTooLargeForATheme_IsGivenUp()
    {
        var (declared, _) = Downloader(length: ThemeAudioDownloader.MaxBytes + 1L);
        var (streamed, _) = Downloader(body: new byte[ThemeAudioDownloader.MaxBytes + 1]);

        await Assert.ThrowsAsync<ThemeRefusedException>(() => declared.DownloadAsync(ScriptedSilo.Base, Parsed(Grant()), ThemeOrigins.Server, _token));
        await Assert.ThrowsAsync<ThemeRefusedException>(() => streamed.DownloadAsync(ScriptedSilo.Base, Parsed(Grant()), ThemeOrigins.Server, _token));
    }
}
