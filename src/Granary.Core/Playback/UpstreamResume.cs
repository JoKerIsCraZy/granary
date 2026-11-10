using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>
/// Picks a direct play body up again at its next byte when the server (or a proxy in front of it) breaks it off or goes
/// silent, and goes on in the same answer to the player, as Silo's Apple app reconnects its reader (its engine's
/// <c>AVIOReader</c>) at the byte it had reached. mpv never sees the break: its own reader counts every retry since the
/// last seek against <c>curl-max-retries</c>, one that brought data too, so a few breaks anywhere in an episode ended the
/// stream, and mpv played out its cache and buffered.
/// Only an answer that can be asked again from any byte is resumed (a 206 with its range, or a 200 that takes ranges and
/// has a length), never a remux. Only bytes of the same file are spliced: the request carries the first answer's
/// validator as If-Range, and only a 206 for exactly the next byte, of the same size and validators, goes on.
/// </summary>
internal sealed class UpstreamResume : IDisposable
{
    /// <summary>Resumes in a row that bring no data before the break reaches the player, as the Apple app's reader allows.</summary>
    internal const int MaxUnproductive = 12;

    /// <summary>Resumes within <see cref="RateWindow"/> that give up: a server that cuts every few kilobytes is not chased.</summary>
    internal const int MaxPerWindow = 30;

    internal static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a break may go without data before it reaches the player, however long each try waits: Silo's Android app
    /// retries a load within 90 s, and the stall watch gives a stream as long.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    // The longest wait before a resume, a server's Retry-After included.
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(15);

    private readonly Func<RangeHeaderValue, string?, CancellationToken, Task<HttpResponseMessage>> _send;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    // The last byte of the answer the player gets, and the file's size when the server named it.
    private readonly long _end;
    private readonly long? _total;
    private readonly EntityTagHeaderValue? _etag;
    private readonly DateTimeOffset? _lastModified;

    // The validator a resume sends as If-Range, as the server wrote it.
    private readonly string _ifRange;

    // The answer whose body is read: the first, then each resumed one. A break ends it, and the next try lets it go.
    private HttpResponseMessage? _current;

    // The last byte the current answer names; whatever its body sends past it is not read.
    private long _answerEnd;

    // The byte the player gets next.
    private long _next;

    // Resumes in a row that brought no data; the first data of a connection starts the count again.
    private int _unproductive;

    // When the break that has brought no data yet began.
    private long? _brokenSince;

    // When the latest tries began, a ring of MaxPerWindow; made at the first break.
    private long[]? _recent;
    private int _tries;

    private UpstreamResume(HttpResponseMessage first, long start, long end, long? total, string ifRange,
        Func<RangeHeaderValue, string?, CancellationToken, Task<HttpResponseMessage>> send, TimeProvider time, ILogger logger)
    {
        _send = send;
        _time = time;
        _logger = logger;
        _current = first;
        _next = start;
        _end = end;
        _answerEnd = end;
        _total = total;
        _ifRange = ifRange;
        _etag = first.Headers.ETag;
        _lastModified = first.Content.Headers.LastModified;
    }

    /// <summary>Bytes of the answer remain: an end before them is a break too.</summary>
    public bool Remains => _next <= _end;

    /// <summary>
    /// The resume of <paramref name="first"/>'s body, or null when it cannot be asked again from a byte: no length, an
    /// encoded body, no ranges (a remux), or nothing to check a resumed answer by. <paramref name="send"/> asks the server
    /// for a range, under an If-Range, until its token ends the try.
    /// </summary>
    public static UpstreamResume? For(HttpResponseMessage first,
        Func<RangeHeaderValue, string?, CancellationToken, Task<HttpResponseMessage>> send, TimeProvider time, ILogger logger)
    {
        var content = first.Content.Headers;
        if (content.ContentLength is not { } length || length <= 0 || content.ContentEncoding.Count > 0)
        {
            return null;
        }
        // Without a validator a changed file of the same size would pass every check of the resumed answer.
        if (IfRange(first) is not { } ifRange)
        {
            return null;
        }
        if (first.StatusCode == HttpStatusCode.PartialContent)
        {
            return content.ContentRange is { From: { } from, To: { } to } range && IsBytes(range) && to - from + 1 == length
                ? new UpstreamResume(first, from, to, range.Length, ifRange, send, time, logger)
                : null;
        }
        return first.StatusCode == HttpStatusCode.OK
            && first.Headers.AcceptRanges.Any(unit => unit.Equals("bytes", StringComparison.OrdinalIgnoreCase))
            ? new UpstreamResume(first, 0, length - 1, length, ifRange, send, time, logger)
            : null;
    }

    /// <summary>
    /// How much of a read of <paramref name="size"/> bytes the current answer may fill: none once the bytes it names are
    /// in, whatever its body sends past them.
    /// </summary>
    public int Room(int size)
    {
        long left = _answerEnd - _next + 1;
        return left >= size ? size : (int)Math.Max(left, 0);
    }

    /// <summary>
    /// How long a resume waits after <paramref name="unproductive"/> in a row brought nothing: not at all after a break,
    /// then 1, 2, 4 and 8 s, or the server's <paramref name="retryAfter"/> when longer, up to 15 s, as the Apple app paces
    /// its reader.
    /// </summary>
    internal static TimeSpan Backoff(int unproductive, TimeSpan retryAfter)
    {
        var ladder = unproductive <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(1 << (Math.Min(unproductive, 4) - 1));
        var wait = ladder > retryAfter ? ladder : retryAfter;
        return wait < MaxWait ? wait : MaxWait;
    }

    /// <summary>The player got <paramref name="count"/> more bytes, on a connection that delivers.</summary>
    public void Delivered(int count)
    {
        _next += count;
        _unproductive = 0;
        _brokenSince = null;
    }

    /// <summary>
    /// The rest of the body from its next byte, once <paramref name="failure"/> broke it off. Throws
    /// <paramref name="failure"/> when it cannot go on, so the player sees the break and its own recovery runs.
    /// </summary>
    public async Task<Stream> ResumeAsync(MediaUpstreamException failure, CancellationToken cancellationToken)
    {
        long at = _next;
        _brokenSince ??= _time.GetTimestamp();
        var left = Budget - _time.GetElapsedTime(_brokenSince.Value);
        if (left <= TimeSpan.Zero)
        {
            throw GiveUp(failure, at, BudgetSpent);
        }
        using var deadline = new CancellationTokenSource(left, _time);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ResumeWithinAsync(failure, at, limit.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw GiveUp(failure, at, BudgetSpent);
        }
    }

    public void Dispose() => _current?.Dispose();

    private static string BudgetSpent => string.Create(CultureInfo.InvariantCulture, $"{Budget.TotalSeconds:F0} s brought nothing");

    // The answer runs to the file's end: the resume asks the rest, as the player asked it.
    private bool RunsToTheEnd => _total is { } total && _end == total - 1;

    // Tries until one goes on, each in its turn; limit ends a wait or a try once the break's budget is spent.
    private async Task<Stream> ResumeWithinAsync(MediaUpstreamException failure, long at, CancellationToken limit, CancellationToken closing)
    {
        var retryAfter = TimeSpan.Zero;
        while (true)
        {
            if (_unproductive >= MaxUnproductive)
            {
                throw GiveUp(failure, at, string.Create(CultureInfo.InvariantCulture, $"{MaxUnproductive} tries brought nothing"));
            }
            var wait = Backoff(_unproductive, retryAfter);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time, limit).ConfigureAwait(false);
            }
            if (TooOften())
            {
                throw GiveUp(failure, at, string.Create(CultureInfo.InvariantCulture, $"{MaxPerWindow} tries within {RateWindow.TotalSeconds:F0} s"));
            }
            _unproductive++;
            _current?.Dispose();
            _current = await TrySendAsync(failure, at, limit, closing).ConfigureAwait(false);
            retryAfter = TimeSpan.Zero;
            if (_current is not { } answer)
            {
                continue;
            }
            if (answer.StatusCode == HttpStatusCode.PartialContent)
            {
                if (Mismatch(answer) is { } mismatch)
                {
                    throw GiveUp(failure, at, mismatch);
                }
                _answerEnd = answer.Content.Headers.ContentRange!.To!.Value;
                _logger.LogInformation("The server {Failure} at byte {Offset}; resumed", failure.Message, at);
                return await answer.Content.ReadAsStreamAsync(closing).ConfigureAwait(false);
            }
            int status = (int)answer.StatusCode;
            // A 401 that the send's one renewal did not cure, and a 403, give up at once, on purpose unlike the Apple
            // app's reader, which gives a refusal of the credentials a short ladder: the renewal has asked for new headers
            // already, and the player's own request then meets the refusal, which the playback answers with a reload.
            if (!IsPassing(status))
            {
                throw GiveUp(failure, at, string.Create(CultureInfo.InvariantCulture, $"answered {status}"));
            }
            _logger.LogDebug("Resuming at byte {Offset} failed: the server answered {Status}", at, status);
            retryAfter = RetryAfter(answer);
        }
    }

    // One try; null when it failed on the way, which the next one may not.
    private async Task<HttpResponseMessage?> TrySendAsync(MediaUpstreamException failure, long at, CancellationToken limit, CancellationToken closing)
    {
        try
        {
            return await _send(new RangeHeaderValue(_next, RunsToTheEnd ? null : _end), _ifRange, limit).ConfigureAwait(false);
        }
        catch (MediaAuthorizationException ex) when (ex.Failure != MediaAuthorizationFailure.Unavailable)
        {
            throw GiveUp(failure, at, "no media headers: " + ex.Failure);
        }
        catch (Exception ex) when (ex is MediaAuthorizationException or IOException or HttpRequestException && !closing.IsCancellationRequested)
        {
            // No token for now, a lost connection, or a cut: the server may be back in a moment.
            _logger.LogDebug("Resuming at byte {Offset} failed: {Failure}", at, Reason(ex));
            return null;
        }
    }

    // A server that cuts every few kilobytes would have each resume deliver and so never wait: more than MaxPerWindow
    // tries within RateWindow are too many. Counts this try when it is not.
    private bool TooOften()
    {
        _recent ??= new long[MaxPerWindow];
        long now = _time.GetTimestamp();
        int slot = _tries % MaxPerWindow;
        if (_tries >= MaxPerWindow && _time.GetElapsedTime(_recent[slot], now) < RateWindow)
        {
            return true;
        }
        _recent[slot] = now;
        _tries++;
        return false;
    }

    // Why a 206 is not the next bytes of the same file, or null when it is.
    private string? Mismatch(HttpResponseMessage answer)
    {
        var content = answer.Content.Headers;
        if (content.ContentRange is not { From: { } from, To: { } to } range || !IsBytes(range) || from != _next || to < from
            || to > _end || (_total is { } total && range.Length != total) || (content.ContentLength is { } length && length != to - from + 1))
        {
            return "answered another range";
        }
        if (content.ContentEncoding.Count > 0)
        {
            return "answered an encoded body";
        }
        // A validator the first answer had must come back unchanged; one that is missing proves nothing.
        bool otherTag = _etag is not null && !_etag.Equals(answer.Headers.ETag);
        bool otherDate = _lastModified is { } before && content.LastModified != before;
        return otherTag || otherDate ? "the file changed" : null;
    }

    private TimeSpan RetryAfter(HttpResponseMessage answer) => answer.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - _time.GetUtcNow(),
        _ => TimeSpan.Zero,
    };

    private MediaUpstreamException GiveUp(MediaUpstreamException failure, long at, string reason)
    {
        _logger.LogInformation("The server {Failure} at byte {Offset}; resuming failed ({Reason}), giving up", failure.Message, at, reason);
        return failure;
    }

    // Busy or failing for a moment, as the Apple app's reader takes them: a 509 is a host at its bandwidth limit, the
    // others a server, or a proxy in front of it, that cannot answer just now.
    private static bool IsPassing(int status) => status is 429 or 500 or 502 or 503 or 504 or 509;

    private static bool IsBytes(ContentRangeHeaderValue range) => range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase);

    // The first answer's validator for If-Range, as the server wrote it, read before anything parses it: its strong tag,
    // or else its date, as a weak tag never matches there (RFC 9110, 13.1.5). Null when it has neither.
    private static string? IfRange(HttpResponseMessage first)
    {
        string? writtenTag = Written(first.Headers.NonValidated, "ETag");
        string? writtenDate = Written(first.Content.Headers.NonValidated, "Last-Modified");
        return first.Headers.ETag is { IsWeak: false } ? writtenTag : writtenDate;
    }

    private static string? Written(HttpHeadersNonValidated headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.ToString() : null;

    // What went wrong, never the message of a request's failure: some carry the address.
    private static string Reason(Exception ex) => ex switch
    {
        MediaUpstreamException upstream => upstream.Message,
        MediaAuthorizationException authorization => "no media headers: " + authorization.Failure,
        _ => MediaRoute.Kind(ex),
    };
}
