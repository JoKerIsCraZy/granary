using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Granary.Core.Playback;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>The loopback proxy between mpv and the server: a real listener, a scripted server behind it.</summary>
public sealed class MediaProxyTests : IDisposable
{
    private static readonly Uri Stream = new("https://nas.example/silo/api/v2/stream/s1");
    private static readonly Uri Playlist = new("https://nas.example/silo/api/v2/playback/transcode/s1/master.m3u8");

    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly List<HttpRequestMessage> _sent = [];
    private readonly HttpClient _player = new();
    private readonly List<bool> _asked = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _server = _ => Text(HttpStatusCode.OK, "media");
    private int _tokens;

    public MediaProxyTests()
    {
        Proxy = new MediaProxy(new FakeHttpMessageHandler((request, _) =>
        {
            _sent.Add(request);
            return Task.FromResult(_server(request));
        }), _time, _logs.For<MediaProxy>());
    }

    private MediaProxy Proxy { get; }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARequest_GoesToTheStream_WithTheHeaders_AndItsRange()
    {
        _server = _ => Partial("data", "bytes 100-103/5000");
        using var route = Open(Stream);

        using var request = new HttpRequestMessage(HttpMethod.Get, route.LocalUrl);
        request.Headers.Range = new RangeHeaderValue(100, null);
        using var response = await _player.SendAsync(request, Cancellation);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("data", await response.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal("bytes 100-103/5000", response.Content.Headers.ContentRange!.ToString());
        var sent = Assert.Single(_sent);
        Assert.Equal(Stream.OriginalString, sent.RequestUri!.OriginalString);
        Assert.Equal("Bearer t1", sent.Headers.Authorization!.ToString());
        Assert.Equal("7", Assert.Single(sent.Headers.GetValues("X-Profile-Id")));
        Assert.Equal("bytes=100-", sent.Headers.Range!.ToString());
    }

    [Fact]
    public void TheLocalUrl_CarriesNoCredential()
    {
        using var route = Open(Stream);

        Assert.Equal("127.0.0.1", route.LocalUrl.Host);
        Assert.DoesNotContain("t1", route.LocalUrl.OriginalString, StringComparison.Ordinal);
        Assert.EndsWith("/silo/api/v2/stream/s1", route.LocalUrl.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedToken_IsRenewed_AndTheRequestRepeated()
    {
        _server = request => request.Headers.Authorization!.Parameter == "t1" ? Text(HttpStatusCode.Unauthorized, "") : Text(HttpStatusCode.OK, "media");
        using var route = Open(Stream);

        string body = await _player.GetStringAsync(route.LocalUrl, Cancellation);

        Assert.Equal("media", body);
        Assert.Equal([false, true], _asked);
        Assert.Equal(["Bearer t1", "Bearer t2"], _sent.Select(r => r.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task ATokenRefusedAgain_ReachesThePlayer()
    {
        // mpv then reports the refusal, and the playback reloads or explains it.
        _server = _ => Text(HttpStatusCode.Unauthorized, "");
        using var route = Open(Stream);

        using var response = await _player.GetAsync(route.LocalUrl, Cancellation);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task NoLogin_IsARefusal()
    {
        using var route = Proxy.Open(Stream, MediaRouteScope.File,
            (_, _) => throw new MediaAuthorizationException(MediaAuthorizationFailure.SignedOut, "Sign in to play from this server."));

        using var response = await _player.GetAsync(route.LocalUrl, Cancellation);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task ATokenNearItsEnd_IsRenewedBeforeItGoesOut()
    {
        using var route = Proxy.Open(Stream, MediaRouteScope.File, Credentials, Headers("t0", _time.GetUtcNow().AddSeconds(30)));

        await _player.GetStringAsync(route.LocalUrl, Cancellation);

        Assert.Equal("Bearer t1", Assert.Single(_sent).Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task AWrongSecret_FindsNothing()
    {
        using var route = Open(Stream);
        var local = route.LocalUrl.OriginalString;
        int secretStart = local.IndexOf('/', "http://".Length) + 1;
        string guessed = local[..secretStart] + new string('0', 64) + local[(secretStart + 64)..];

        using var response = await _player.GetAsync(guessed, Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData("segment/seg_001.ts", true)]
    [InlineData("v1/index.m3u8", true)]
    [InlineData("../../stream/other", false)]
    [InlineData("%2e%2e/%2e%2e/stream/other", false)]
    [InlineData("segment%2f..%2fx", false)]
    public async Task AnHlsRoute_ReachesWhatLiesBesideThePlaylist_AndNothingElse(string relative, bool reachable)
    {
        using var route = Open(Playlist, MediaRouteScope.Directory);
        string local = route.LocalUrl.OriginalString;
        string beside = local[..(local.LastIndexOf('/') + 1)] + relative;

        using var response = await _player.GetAsync(beside, Cancellation);

        Assert.Equal(reachable ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(reachable ? 1 : 0, _sent.Count);
    }

    [Fact]
    public async Task AProgressiveRoute_ReachesItsOwnUrlOnly()
    {
        using var route = Open(new Uri("https://nas.example/silo/api/v2/stream/s1?seek=600"));
        string other = route.LocalUrl.OriginalString.Replace("seek=600", "seek=0", StringComparison.Ordinal);

        using var own = await _player.GetAsync(route.LocalUrl, Cancellation);
        using var changed = await _player.GetAsync(other, Cancellation);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, changed.StatusCode);
        Assert.Equal("https://nas.example/silo/api/v2/stream/s1?seek=600", Assert.Single(_sent).RequestUri!.OriginalString);
    }

    [Fact]
    public async Task APathGoesUpstreamAsWritten()
    {
        // Byte for byte (9.2): %41 must not become "A", nor %7E "~".
        using var route = Open(Playlist, MediaRouteScope.Directory);
        string local = route.LocalUrl.OriginalString;
        var beside = new Uri(local[..(local.LastIndexOf('/') + 1)] + "segment/%41b%7E.ts?x=%2B",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });

        using var response = await _player.GetAsync(beside, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/silo/api/v2/playback/transcode/s1/segment/%41b%7E.ts?x=%2B", Assert.Single(_sent).RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task ARenewalThatBringsNothingNew_WaitsBeforeTheNextTry()
    {
        // The server cannot refresh for now and the old token still plays: segments must not each wait for a refresh.
        int renewals = 0;
        var ending = Headers("t0", _time.GetUtcNow().AddSeconds(30));
        using var route = Proxy.Open(Stream, MediaRouteScope.File, (_, _) =>
        {
            renewals++;
            return Task.FromResult(ending);
        }, ending);

        await _player.GetStringAsync(route.LocalUrl, Cancellation);
        await _player.GetStringAsync(route.LocalUrl, Cancellation);
        _time.Advance(TimeSpan.FromSeconds(5));
        await _player.GetStringAsync(route.LocalUrl, Cancellation);

        Assert.Equal(2, renewals);
    }

    [Fact]
    public async Task NoTokenForNow_IsABusyServer_NotARefusal()
    {
        using var route = Proxy.Open(Stream, MediaRouteScope.File,
            (_, _) => throw new MediaAuthorizationException(MediaAuthorizationFailure.Unavailable, "Try again in a moment."));

        using var response = await _player.GetAsync(route.LocalUrl, Cancellation);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task AServerThatDoesNotAnswer_LeavesAnAbortedRequest_NotAnEmptyFile()
    {
        _server = _ => throw new IOException("connection reset");
        using var route = Open(Stream);

        // An empty 200 would read as a 0-byte file; the player must see the request fail.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _player.GetAsync(route.LocalUrl, Cancellation));
    }

    [Fact]
    public async Task AClosedRoute_FindsNothing()
    {
        var route = Open(Stream);
        route.Dispose();

        using var response = await _player.GetAsync(route.LocalUrl, Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ARedirect_IsNotFollowed_AndItsTargetNotShown()
    {
        _server = _ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://elsewhere.example/steal");
            return redirect;
        };
        using var route = Open(Stream);

        using var response = await _player.GetAsync(route.LocalUrl, Cancellation);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task OnlyTheHeadersThePlayerNeeds_ComeBack()
    {
        _server = _ =>
        {
            var response = Text(HttpStatusCode.OK, "media");
            response.Headers.Add("Set-Cookie", "session=1");
            response.Headers.Add("Accept-Ranges", "bytes");
            return response;
        };
        using var route = Open(Stream);

        using var response = await _player.GetAsync(route.LocalUrl, Cancellation);

        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("bytes", Assert.Single(response.Headers.AcceptRanges));
    }

    [Fact]
    public async Task AStreamOfUnknownLength_ArrivesWhole()
    {
        _server = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream("remuxed")) };
        using var route = Open(Stream);

        Assert.Equal("remuxed", await _player.GetStringAsync(route.LocalUrl, Cancellation));
    }

    [Fact]
    public async Task Head_AnswersWithoutABody()
    {
        _server = request => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) { Headers = { ContentLength = 5000 } } }
            : Text(HttpStatusCode.BadRequest, "");
        using var route = Open(Stream);

        using var request = new HttpRequestMessage(HttpMethod.Head, route.LocalUrl);
        using var response = await _player.SendAsync(request, Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5000, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task OtherMethods_AreRefused()
    {
        using var route = Open(Stream);

        using var response = await _player.PostAsync(route.LocalUrl, new StringContent(""), Cancellation);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(_sent);
    }

    public void Dispose()
    {
        _player.Dispose();
        Proxy.Dispose();
    }

    private IMediaRoute Open(Uri upstream, MediaRouteScope scope = MediaRouteScope.File) => Proxy.Open(upstream, scope, Credentials);

    // Each renewal hands out the next token: t1, t2, ...
    private Task<MediaCredentials> Credentials(bool rejected, CancellationToken cancellationToken)
    {
        _asked.Add(rejected);
        return Task.FromResult(Headers("t" + ++_tokens, _time.GetUtcNow().AddHours(8)));
    }

    private static MediaCredentials Headers(string token, DateTimeOffset expiresAt) =>
        new([new("Authorization", "Bearer " + token), new("X-Profile-Id", "7")], expiresAt);

    private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "video/x-matroska") };

    private static HttpResponseMessage Partial(string body, string range)
    {
        var response = Text(HttpStatusCode.PartialContent, body);
        response.Content.Headers.ContentRange = ContentRangeHeaderValue.Parse(range);
        return response;
    }

    // A body whose length is unknown, as a progressive remux streams it.
    private sealed class UnknownLengthStream(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        public override bool CanSeek => false;
    }
}
