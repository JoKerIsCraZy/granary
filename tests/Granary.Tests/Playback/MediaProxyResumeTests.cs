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

    private static readonly DateTimeOffset Modified = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    // A test whose proxy waits for a clock nobody moves fails instead of hanging.
    private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(10);

    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly HttpClient _player = new();
    private readonly List<bool> _asked = [];
    private readonly FakeHttpMessageHandler _server;
    private Func<HttpRequestMessage, HttpResponseMessage> _answer = _ => throw new InvalidOperationException("No answer scripted.");

    // Requests the server takes and never answers.
    private Func<HttpRequestMessage, bool> _hangs = _ => false;

    // Renewing the headers fails so, when set.
    private MediaAuthorizationFailure? _renewalFails;
    private int _tokens;

    public MediaProxyResumeTests()
    {
        _server = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (_hangs(request))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return _answer(request);
        });
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
    public async Task ThePlayer_KeepsTheFirstAnswersStatusAndHeaders()
    {
        _answer = request => Sent.Count == 1 ? Part(100, breakAfter: 300) : Part(From(request));
        using var route = Open();
        using var request = Ask(route, from: 100, to: null);

        using var response = await _player.SendAsync(request, Cancellation).WaitAsync(HangLimit, Cancellation);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 100-4999/5000", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(Size - 100, response.Content.Headers.ContentLength);
        Assert.Equal(Movie[100..], await response.Content.ReadAsByteArrayAsync(Cancellation));
    }

    [Fact]
    public async Task AResume_AsksItsOwnCondition_NotThePlayers()
    {
        // The player's conditions could answer a 304 or a 412 in the middle of a body.
        _answer = request => Sent.Count == 1 ? Part(100, breakAfter: 300) : Part(From(request));
        using var route = Open();
        using var request = Ask(route, from: 100, to: null);
        request.Headers.IfRange = new RangeConditionHeaderValue(Modified);
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"v0\""));
        request.Headers.UserAgent.ParseAdd("mpv/0.41");

        using var response = await _player.SendAsync(request, Cancellation).WaitAsync(HangLimit, Cancellation);

        Assert.Equal(Movie[100..], await response.Content.ReadAsByteArrayAsync(Cancellation));
        Assert.Equal(Modified, Sent[0].Headers.IfRange!.Date);
        var resume = Sent[1];
        Assert.Equal("\"v1\"", resume.Headers.IfRange!.ToString());
        Assert.Empty(resume.Headers.IfNoneMatch);
        Assert.Equal("mpv/0.41", resume.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task AWholeFileThatTakesRanges_GoesOnUnderItsDate_AsTheServerWroteIt()
    {
        // A weak tag never matches in If-Range: the date guards instead, sent back as it came.
        const string Written = "Tuesday, 01-Sep-26 08:00:00 GMT";
        _answer = request =>
        {
            if (Sent.Count > 1)
            {
                return Part(From(request), etag: "W/\"v1\"", modified: Modified);
            }
            var whole = Whole(breakAfter: 1234);
            whole.Content.Headers.TryAddWithoutValidation("Last-Modified", Written);
            return whole;
        };
        using var route = Open();

        byte[] got = await PlayAsync(route, from: null);

        Assert.Equal(Movie, got);
        Assert.Equal("bytes=1234-", Sent[1].Headers.Range!.ToString());
        Assert.Equal(Written, Sent[1].Headers.NonValidated["If-Range"].ToString());
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
    public async Task AResumedAnswerThatEndsBeforeItsRange_IsResumedAgain()
    {
        // Servers cap their answers: the rest is asked from where the capped one ended.
        _answer = request => Sent.Count switch
        {
            1 => Part(100, breakAfter: 300),
            2 => Part(400, to: 999),
            _ => Part(From(request)),
        };
        using var route = Open();

        byte[] got = await PlayAsync(route, from: 100);

        Assert.Equal(Movie[100..], got);
        Assert.Equal(["bytes=400-", "bytes=1000-"], Sent.Skip(1).Select(r => r.Headers.Range!.ToString()));
        Assert.Contains("The server ended early at byte 1000; resumed", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBrokenAnswer_IsLetGo_OnceAResumeReplacesIt()
    {
        var broken = new Body(Movie.AsMemory(100), breakAfter: 300, silent: false);
        bool? letGoBeforeTheResume = null;
        _answer = request =>
        {
            if (Sent.Count == 1)
            {
                return Partial(broken, 100, Size - 1, Size, "\"v1\"", modified: null);
            }
            letGoBeforeTheResume = broken.Disposed;
            return Part(From(request));
        };
        using var route = Open();

        await PlayAsync(route, from: 100);

        Assert.True(letGoBeforeTheResume);
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
    public async Task MoreThanThirtyResumesWithinAMinute_GiveUp()
    {
        // A server that cuts every few bytes would have every resume deliver, unpaced, for good.
        _answer = request => Part(From(request), breakAfter: 10);
        using var route = Open();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => PlayAsync(route, from: 100));

        Assert.Equal(1 + UpstreamResume.MaxPerWindow, Sent.Count);
        Assert.Contains("resuming failed (30 tries within 60 s), giving up", _logs.All, StringComparison.Ordinal);
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

    [Fact]
    public async Task AServerThatAnswersNoResume_IsGivenUp_90SecondsAfterTheBreak()
    {
        // Each try could wait the idle limit: the break as a whole gets 90 s, as long as the stall watch gives a stream.
        _answer = _ => Part(100, breakAfter: 300);
        _hangs = _ => Sent.Count > 1;
        using var route = Open();
        var playing = PlayAsync(route, from: 100);

        await ServerPlaybackFixture.Until(() => Sent.Count == 2 && _time.NextDue == MediaRoute.UpstreamIdleTimeout, "the first resume waits");
        _time.Advance(MediaRoute.UpstreamIdleTimeout);
        await ServerPlaybackFixture.Until(() => _time.NextDue == TimeSpan.FromSeconds(1), "the next resume waits its turn");
        _time.Advance(TimeSpan.FromSeconds(1));
        await ServerPlaybackFixture.Until(() => Sent.Count == 3 && _time.NextDue == TimeSpan.FromSeconds(29), "the second resume waits");
        _time.Advance(TimeSpan.FromSeconds(29));

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => playing);
        Assert.Equal(3, Sent.Count);
        Assert.Contains("at byte 400; resuming failed (90 s brought nothing), giving up", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingTheRoute_EndsAResumeThatWaits_AtOnce()
    {
        _answer = _ => Sent.Count == 1 ? Part(100, breakAfter: 300) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var route = Open();
        var playing = PlayAsync(route, from: 100);
        await _time.WaitUntilAsync(() => _time.NextDue == TimeSpan.FromSeconds(1), Cancellation);

        route.Dispose();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => playing);
        await ServerPlaybackFixture.Until(() => _time.NextDue is null, "no wait is left");
        Assert.Equal(2, Sent.Count);
    }

    [Theory]
    [InlineData(503, null, 1)]
    [InlineData(500, null, 1)]
    [InlineData(502, null, 1)]
    [InlineData(504, null, 1)]
    [InlineData(429, 5, 5)]
    [InlineData(509, 60, 15)]
    public async Task ABusyServer_IsAskedAgain_AfterItsRetryAfter_UpTo15Seconds(int status, int? retryAfter, int waitSeconds)
    {
        _answer = request => Sent.Count switch
        {
            1 => Part(100, breakAfter: 300),
            2 => Busy(status, retryAfter is { } seconds ? new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)) : null),
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
    [InlineData(-600, 1)]
    [InlineData(5, 5)]
    public async Task ARetryAfterDate_CountsFromNow(int fromNow, int waitSeconds)
    {
        _answer = request => Sent.Count switch
        {
            1 => Part(100, breakAfter: 300),
            2 => Busy(503, new RetryConditionHeaderValue(_time.GetUtcNow().AddSeconds(fromNow))),
            _ => Part(From(request)),
        };
        using var route = Open();
        var playing = PlayAsync(route, from: 100);

        var wait = TimeSpan.FromSeconds(waitSeconds);
        await _time.WaitUntilAsync(() => _time.NextDue == wait, Cancellation);
        _time.Advance(wait);

        Assert.Equal(Movie[100..], await playing);
    }

    [Theory]
    [InlineData("the whole file")]
    [InlineData("another start")]
    [InlineData("another size")]
    [InlineData("no size")]
    [InlineData("past its end")]
    [InlineData("an unsatisfied range")]
    [InlineData("several ranges")]
    [InlineData("another tag")]
    [InlineData("no tag")]
    [InlineData("another date")]
    [InlineData("no date")]
    [InlineData("no such range")]
    [InlineData("gone")]
    [InlineData("not implemented")]
    public async Task AnAnswerThatIsNotTheNextBytesOfTheSameFile_IsNotSplicedIn(string answer)
    {
        // A 200 is the server saying the file changed (If-Range): the player has to see the break and load again.
        _answer = request => Sent.Count == 1 ? Part(100, to: 3999, breakAfter: 300, modified: Modified) : answer switch
        {
            "the whole file" => Whole(),
            "another start" => Part(From(request) + 50, to: 3999, modified: Modified),
            "another size" => Part(From(request), to: 3999, total: Size + 1000, modified: Modified),
            "no size" => Part(From(request), to: 3999, total: null, modified: Modified),
            "past its end" => Part(From(request), modified: Modified),
            "an unsatisfied range" => Unsatisfied(),
            "several ranges" => SeveralRanges(),
            "another tag" => Part(From(request), to: 3999, etag: "\"v2\"", modified: Modified),
            "no tag" => Part(From(request), to: 3999, etag: null, modified: Modified),
            "another date" => Part(From(request), to: 3999, modified: Modified.AddHours(1)),
            "no date" => Part(From(request), to: 3999),
            "no such range" => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable),
            "gone" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.NotImplemented),
        };
        using var route = Open();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => PlayAsync(route, from: 100, to: 3999));

        Assert.Equal(2, Sent.Count);
        Assert.Equal("bytes=400-3999", Sent[1].Headers.Range!.ToString());
        Assert.Contains("at byte 400; resuming failed", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoHeadersForNow_IsAskedAgainOnTheLadder()
    {
        // The server could not issue a token at that moment: a busy server, not a refusal.
        _renewalFails = MediaAuthorizationFailure.Unavailable;
        _answer = request => Sent.Count switch
        {
            1 => Part(100, breakAfter: 300),
            2 => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => Part(From(request)),
        };
        using var route = Open();
        var playing = PlayAsync(route, from: 100);

        await _time.WaitUntilAsync(() => _time.NextDue == TimeSpan.FromSeconds(1), Cancellation);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(Movie[100..], await playing);
        Assert.Equal(3, Sent.Count);
        Assert.Equal([false, true], _asked);
    }

    [Fact]
    public async Task AResumeWithoutALogin_GivesUpAtOnce()
    {
        _renewalFails = MediaAuthorizationFailure.SignedOut;
        _answer = _ => Sent.Count == 1 ? Part(100, breakAfter: 300) : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        using var route = Open();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => PlayAsync(route, from: 100));

        Assert.Equal(2, Sent.Count);
        Assert.Contains("resuming failed (no media headers: SignedOut), giving up", _logs.All, StringComparison.Ordinal);
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

    // Each renewal hands out the next token: t1, t2, ...; or fails, when the test says so.
    private Task<MediaCredentials> Credentials(bool rejected, CancellationToken cancellationToken)
    {
        _asked.Add(rejected);
        if (rejected && _renewalFails is { } failure)
        {
            return Task.FromException<MediaCredentials>(new MediaAuthorizationException(failure, "No headers."));
        }
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
        long? to = null, long? total = Size, DateTimeOffset? modified = null)
    {
        long end = to ?? total!.Value - 1;
        var body = new Body(Movie.AsMemory((int)from, (int)(end - from + 1)), breakAfter, silent);
        return Partial(body, from, end, total, etag, modified);
    }

    private static HttpResponseMessage Partial(Body body, long from, long end, long? total, string? etag, DateTimeOffset? modified)
    {
        var response = Answer(HttpStatusCode.PartialContent, body, end - from + 1);
        response.Content.Headers.ContentRange = total is { } length
            ? new ContentRangeHeaderValue(from, end, length)
            : new ContentRangeHeaderValue(from, end);
        response.Content.Headers.LastModified = modified;
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }
        return response;
    }

    // The whole movie, as direct play answers a request without a range.
    private static HttpResponseMessage Whole(int? breakAfter = null)
    {
        var response = Answer(HttpStatusCode.OK, new Body(Movie, breakAfter, silent: false), Size);
        response.Headers.AcceptRanges.Add("bytes");
        response.Headers.ETag = EntityTagHeaderValue.Parse("W/\"v1\"");
        return response;
    }

    // A 206 that names no range of its own, only the size: "bytes */5000".
    private static HttpResponseMessage Unsatisfied()
    {
        var response = Answer(HttpStatusCode.PartialContent, new Body(Movie.AsMemory(0, 10), null, silent: false), 10);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(Size);
        response.Headers.ETag = EntityTagHeaderValue.Parse("\"v1\"");
        return response;
    }

    // A multipart answer of several ranges, whose ranges are inside the body.
    private static HttpResponseMessage SeveralRanges()
    {
        var response = Answer(HttpStatusCode.PartialContent, new Body(Movie.AsMemory(400, 100), null, silent: false), 100);
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/byteranges; boundary=x");
        response.Headers.ETag = EntityTagHeaderValue.Parse("\"v1\"");
        return response;
    }

    // A progressive remux: chunked, no length, no ranges.
    private static HttpResponseMessage Remux(int breakAfter) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new Body(Movie, breakAfter, silent: false)),
    };

    private static HttpResponseMessage Answer(HttpStatusCode status, Body body, long length)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(body) };
        response.Content.Headers.ContentLength = length;
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
        return response;
    }

    private static HttpResponseMessage Busy(int status, RetryConditionHeaderValue? retryAfter)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.RetryAfter = retryAfter;
        return response;
    }

    // The server's side of a body: its bytes, in reads as large as asked, until it breaks off (or goes silent) after some.
    private sealed class Body(ReadOnlyMemory<byte> bytes, int? breakAfter, bool silent) : System.IO.Stream
    {
        private int _sent;

        public bool Disposed { get; private set; }

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

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
