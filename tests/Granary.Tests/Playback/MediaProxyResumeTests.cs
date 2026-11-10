using System.Net;
using System.Net.Http.Headers;
using Granary.Core.Playback;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>
/// The media proxy picks a direct play body up again at its next byte when the server breaks it off, in the same answer
/// to the player: a real listener, a scripted server behind it.
/// </summary>
public sealed class MediaProxyResumeTests : IDisposable
{
    private const int Size = 5000;

    private static readonly Uri Stream = new("https://nas.example/silo/api/v2/stream/s1");

    // A file whose bytes repeat only every 251, so a byte sent twice or left out shows.
    private static readonly byte[] Movie = [.. Enumerable.Range(0, Size).Select(i => (byte)(i % 251))];

    // A test whose proxy waits for a clock nobody moves fails instead of hanging.
    private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(10);

    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly HttpClient _player = new();
    private readonly List<bool> _asked = [];
    private readonly FakeHttpMessageHandler _server;
    private Func<HttpRequestMessage, HttpResponseMessage> _answer = _ => throw new InvalidOperationException("No answer scripted.");
    private int _tokens;

    public MediaProxyResumeTests()
    {
        _server = new FakeHttpMessageHandler((request, _) => Task.FromResult(_answer(request)));
        Proxy = new MediaProxy(_server, _time, _logs.For<MediaProxy>());
    }

    private MediaProxy Proxy { get; }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // The requests the server got so far; the first is the player's own.
    private IReadOnlyList<HttpRequestMessage> Sent => _server.Requests;

    [Fact]
    public async Task ABodyTheServerBreaksOff_GoesOnFromItsNextByte_InTheSameAnswer()
    {
        _answer = request => Sent.Count == 1 ? Part(100, breakAfter: 300) : Part(From(request));
        using var route = Open();

        byte[] got = await PlayAsync(route, from: 100);

        Assert.Equal(Movie[100..], got);
        Assert.Equal(2, Sent.Count);
        var resume = Sent[1];
        Assert.Equal("bytes=400-", resume.Headers.Range!.ToString());
        // The first answer's tag: bytes of a file that changed meanwhile are never spliced in.
        Assert.Equal("\"v1\"", resume.Headers.IfRange!.ToString());
        Assert.Equal("Bearer t1", resume.Headers.Authorization!.ToString());
        Assert.Contains("Information MediaProxy: The server broke off (IOException) at byte 400; resumed", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWholeFileThatTakesRanges_GoesOnUnderItsDate_WhenItsTagIsWeak()
    {
        var modified = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        _answer = request => Sent.Count == 1
            ? Whole(breakAfter: 1234, modified)
            : Part(From(request), etag: "W/\"v1\"", modified: modified);
        using var route = Open();

        byte[] got = await PlayAsync(route, from: null);

        Assert.Equal(Movie, got);
        var resume = Sent[1];
        Assert.Equal("bytes=1234-", resume.Headers.Range!.ToString());
        // A weak tag never matches in If-Range; the date guards instead.
        Assert.Equal(modified, resume.Headers.IfRange!.Date);
    }

    [Fact]
    public async Task ARangeThatEndsBeforeTheFile_IsResumedToItsOwnEnd()
    {
        _answer = request => Sent.Count == 1 ? Part(100, to: 1999, breakAfter: 500) : Part(From(request), to: 1999);
        using var route = Open();

        byte[] got = await PlayAsync(route, from: 100, to: 1999);

        Assert.Equal(Movie[100..2000], got);
        Assert.Equal("bytes=600-1999", Sent[1].Headers.Range!.ToString());
    }

    [Fact]
    public async Task EveryBreakOfOneAnswer_IsPickedUp_AsLongAsEachResumeBringsData()
    {
        // More breaks than the resumes allowed in a row: each resume that delivers starts the count again.
        _answer = request => Part(From(request), breakAfter: Sent.Count <= 20 ? 100 : null);
        using var route = Open();

        byte[] got = await PlayAsync(route, from: 100);

        Assert.Equal(Movie[100..], got);
        Assert.Equal(21, Sent.Count);
        Assert.Equal(Enumerable.Range(1, 20).Select(n => 100 + (100L * n)), Sent.Skip(1).Select(From));
    }

    [Fact]
    public async Task TwelveResumesInARowThatBringNothing_GiveUp_AndThePlayerSeesTheBreak()
    {
        // At once after the break, then 1, 2, 4 and 8 s apart.
        _answer = _ => Sent.Count == 1 ? Part(100, breakAfter: 300) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var route = Open();
        var playing = PlayAsync(route, from: 100);

        foreach (int seconds in new[] { 1, 2, 4, 8, 8, 8, 8, 8, 8, 8, 8 })
        {
            var wait = TimeSpan.FromSeconds(seconds);
            await _time.WaitUntilAsync(() => _time.NextDue == wait, Cancellation);
            _time.Advance(wait);
        }

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => playing);
        Assert.Equal(1 + UpstreamResume.MaxUnproductive, Sent.Count);
        Assert.Contains("The server broke off (IOException) at byte 400; resuming failed (12 tries brought nothing), giving up",
            _logs.All, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(503, null, 1)]
    [InlineData(429, 5, 5)]
    [InlineData(509, 60, 15)]
    public async Task ABusyServer_IsAskedAgain_AfterItsRetryAfter_UpTo15Seconds(int status, int? retryAfter, int waitSeconds)
    {
        _answer = request => Sent.Count switch
        {
            1 => Part(100, breakAfter: 300),
            2 => Busy(status, retryAfter),
            _ => Part(From(request)),
        };
        using var route = Open();
        var playing = PlayAsync(route, from: 100);

        var wait = TimeSpan.FromSeconds(waitSeconds);
        await _time.WaitUntilAsync(() => _time.NextDue == wait, Cancellation);
        _time.Advance(wait);

        Assert.Equal(Movie[100..], await playing);
        Assert.Equal("bytes=400-", Sent[2].Headers.Range!.ToString());
    }

    [Theory]
    [InlineData("the whole file")]
    [InlineData("another start")]
    [InlineData("another size")]
    [InlineData("another tag")]
    [InlineData("no such range")]
    public async Task AnAnswerThatIsNotTheNextBytesOfTheSameFile_IsNotSplicedIn(string answer)
    {
        // A 200 is the server saying the file changed (If-Range); the player has to see the break and load again.
        _answer = request => Sent.Count == 1 ? Part(100, breakAfter: 300) : answer switch
        {
            "the whole file" => Whole(),
            "another start" => Part(From(request) + 50),
            "another size" => Part(From(request), to: Size - 1, total: Size + 1000),
            "another tag" => Part(From(request), etag: "\"v2\""),
            _ => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable),
        };
        using var route = Open();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => PlayAsync(route, from: 100));

        Assert.Equal(2, Sent.Count);
        Assert.Contains("at byte 400; resuming failed", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResumeRefusedEvenWithNewHeaders_GivesUpAtOnce()
    {
        // The same refusal would come back however often it is asked: no backoff and no further tries.
        _answer = _ => Sent.Count == 1 ? Part(100, breakAfter: 300) : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        using var route = Open();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => PlayAsync(route, from: 100));

        Assert.Equal(3, Sent.Count);
        Assert.Equal([false, true], _asked);
        Assert.Equal("Bearer t2", Sent[2].Headers.Authorization!.ToString());
        Assert.Contains("resuming failed (answered 401), giving up", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemuxThatBreaksOff_IsNotResumed()
    {
        // No length and no ranges: it cannot be asked again from a byte, and the playback reconnects instead.
        _answer = _ => Remux(breakAfter: 300);
        using var route = Open();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => PlayAsync(route, from: null));

        Assert.Single(Sent);
    }

    [Fact]
    public async Task AServerThatFallsSilent_IsCut_AndPickedUpAtItsNextByte()
    {
        _answer = request => Sent.Count == 1 ? Part(100, breakAfter: 300, silent: true) : Part(From(request));
        using var route = Open();
        using var request = Ask(route, from: 100, to: null);
        using var response = await _player.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Cancellation);
        var body = await response.Content.ReadAsStreamAsync(Cancellation);
        byte[] first = new byte[300];
        await body.ReadExactlyAsync(first, Cancellation);
        await _time.WaitForTimerAsync(Cancellation);

        _time.Advance(MediaRoute.UpstreamIdleTimeout);
        using var rest = new MemoryStream();
        await body.CopyToAsync(rest, Cancellation).WaitAsync(HangLimit, Cancellation);

        byte[] got = [.. first, .. rest.ToArray()];
        Assert.Equal(Movie[100..], got);
        Assert.Equal("bytes=400-", Sent[1].Headers.Range!.ToString());
        Assert.Contains("The server sent nothing for 60 s at byte 400; resumed", _logs.All, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(2, 0, 2)]
    [InlineData(3, 0, 4)]
    [InlineData(4, 0, 8)]
    [InlineData(11, 0, 8)]
    [InlineData(0, 3, 3)]
    [InlineData(4, 30, 15)]
    public void TheWaitBeforeAResume_GrowsWithTheTriesThatBroughtNothing(int unproductive, int retryAfter, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), UpstreamResume.Backoff(unproductive, TimeSpan.FromSeconds(retryAfter)));

    public void Dispose()
    {
        _player.Dispose();
        Proxy.Dispose();
    }

    private IMediaRoute Open() => Proxy.Open(Stream, MediaRouteScope.File, Credentials);

    // Each renewal hands out the next token: t1, t2, ...
    private Task<MediaCredentials> Credentials(bool rejected, CancellationToken cancellationToken)
    {
        _asked.Add(rejected);
        return Task.FromResult(new MediaCredentials(
            [new("Authorization", "Bearer t" + ++_tokens), new("X-Profile-Id", "7")], _time.GetUtcNow().AddHours(8)));
    }

    // The player reads the whole answer, as mpv would; an answer that breaks off fails.
    private async Task<byte[]> PlayAsync(IMediaRoute route, long? from, long? to = null)
    {
        using var request = Ask(route, from, to);
        using var response = await _player.SendAsync(request, Cancellation).WaitAsync(HangLimit, Cancellation);
        return await response.Content.ReadAsByteArrayAsync(Cancellation);
    }

    private static HttpRequestMessage Ask(IMediaRoute route, long? from, long? to)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route.LocalUrl);
        if (from is not null)
        {
            request.Headers.Range = new RangeHeaderValue(from, to);
        }
        return request;
    }

    private static long From(HttpRequestMessage request) => request.Headers.Range!.Ranges.Single().From!.Value;

    // The movie from one byte to another (its end unless named), as direct play answers a range.
    private static HttpResponseMessage Part(long from, int? breakAfter = null, bool silent = false, string? etag = "\"v1\"",
        long? to = null, long total = Size, DateTimeOffset? modified = null)
    {
        long end = to ?? total - 1;
        var response = Answer(HttpStatusCode.PartialContent, Movie.AsMemory((int)from, (int)(end - from + 1)), breakAfter, silent);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, end, total);
        response.Content.Headers.LastModified = modified;
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }
        return response;
    }

    // The whole movie, as direct play answers a request without a range.
    private static HttpResponseMessage Whole(int? breakAfter = null, DateTimeOffset? modified = null)
    {
        var response = Answer(HttpStatusCode.OK, Movie, breakAfter, silent: false);
        response.Headers.AcceptRanges.Add("bytes");
        response.Headers.ETag = EntityTagHeaderValue.Parse("W/\"v1\"");
        response.Content.Headers.LastModified = modified;
        return response;
    }

    // A progressive remux: chunked, no length, no ranges.
    private static HttpResponseMessage Remux(int breakAfter) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new Body(Movie, breakAfter, silent: false)),
    };

    private static HttpResponseMessage Answer(HttpStatusCode status, ReadOnlyMemory<byte> bytes, int? breakAfter, bool silent)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(new Body(bytes, breakAfter, silent)) };
        response.Content.Headers.ContentLength = bytes.Length;
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
        return response;
    }

    private static HttpResponseMessage Busy(int status, int? retryAfter)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        if (retryAfter is { } seconds)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }
        return response;
    }

    // The server's side of a body: its bytes, in reads as large as asked, until it breaks off (or goes silent) after some.
    private sealed class Body(ReadOnlyMemory<byte> bytes, int? breakAfter, bool silent) : System.IO.Stream
    {
        private int _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int until = Math.Min(breakAfter ?? bytes.Length, bytes.Length);
            if (_sent == until && breakAfter is not null)
            {
                if (!silent)
                {
                    throw new IOException("connection reset");
                }
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            int count = Math.Min(buffer.Length, until - _sent);
            bytes.Span.Slice(_sent, count).CopyTo(buffer.Span);
            _sent += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
