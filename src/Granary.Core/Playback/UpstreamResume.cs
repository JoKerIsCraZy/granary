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
/// validator as If-Range, and only a 206 for exactly the next byte, of the same size and validator, goes on.
/// </summary>
internal sealed class UpstreamResume : IDisposable
{
    /// <summary>Resumes in a row that bring no data before the break reaches the player, as the Apple app's reader allows.</summary>
    internal const int MaxUnproductive = 12;

    // The longest wait before a resume, a server's Retry-After included.
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(15);

    private readonly Func<RangeHeaderValue, RangeConditionHeaderValue?, Task<HttpResponseMessage>> _send;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    // The last byte of the answer the player gets, and the file's size when the server named it.
    private readonly long _end;
    private readonly long? _total;
    private readonly EntityTagHeaderValue? _etag;
    private readonly DateTimeOffset? _lastModified;
    private readonly RangeConditionHeaderValue? _ifRange;

    // The latest resumed answer, its body being read; the first one stays with its relay.
    private HttpResponseMessage? _resumed;

    // The byte the player gets next.
    private long _next;

    // Resumes in a row that brought no data; the first data of a connection starts the count again.
    private int _unproductive;

    private UpstreamResume(HttpResponseMessage first, long start, long end, long? total,
        Func<RangeHeaderValue, RangeConditionHeaderValue?, Task<HttpResponseMessage>> send, TimeProvider time, ILogger logger)
    {
        _send = send;
        _time = time;
        _logger = logger;
        _next = start;
        _end = end;
        _total = total;
        _etag = first.Headers.ETag;
        _lastModified = first.Content.Headers.LastModified;
        // A weak tag never matches in If-Range (RFC 9110, 13.1.5): the date is the guard then.
        _ifRange = _etag is { IsWeak: false } strong ? new RangeConditionHeaderValue(strong)
            : _lastModified is { } date ? new RangeConditionHeaderValue(date)
            : null;
    }

    /// <summary>Bytes of the answer remain: an end before them is a break too.</summary>
    public bool Remains => _next <= _end;

    /// <summary>
    /// The resume of <paramref name="first"/>'s body, or null when it cannot be asked again from a byte: no length, an
    /// encoded body, or no ranges (a remux). <paramref name="send"/> asks the server for a range, under a condition.
    /// </summary>
    public static UpstreamResume? For(HttpResponseMessage first,
        Func<RangeHeaderValue, RangeConditionHeaderValue?, Task<HttpResponseMessage>> send, TimeProvider time, ILogger logger)
    {
        var content = first.Content.Headers;
        if (content.ContentLength is not { } length || length <= 0 || content.ContentEncoding.Count > 0)
        {
            return null;
        }
        if (first.StatusCode == HttpStatusCode.PartialContent)
        {
            return content.ContentRange is { From: { } from, To: { } to } range && IsBytes(range) && to - from + 1 == length
                ? new UpstreamResume(first, from, to, range.Length, send, time, logger)
                : null;
        }
        return first.StatusCode == HttpStatusCode.OK
            && first.Headers.AcceptRanges.Any(unit => unit.Equals("bytes", StringComparison.OrdinalIgnoreCase))
            ? new UpstreamResume(first, 0, length - 1, length, send, time, logger)
            : null;
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
    }

    /// <summary>
    /// The rest of the body from its next byte, once <paramref name="failure"/> broke it off. Throws
    /// <paramref name="failure"/> when it cannot go on, so the player sees the break and its own recovery runs.
    /// </summary>
    public async Task<Stream> ResumeAsync(MediaUpstreamException failure, CancellationToken cancellationToken)
    {
        long at = _next;
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
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
            _unproductive++;
            _resumed?.Dispose();
            _resumed = null;
            HttpResponseMessage answer;
            try
            {
                answer = await _send(new RangeHeaderValue(_next, RunsToTheEnd ? null : _end), _ifRange).ConfigureAwait(false);
            }
            catch (MediaAuthorizationException ex)
            {
                throw GiveUp(failure, at, "no media headers: " + ex.Failure);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Resuming at byte {Offset} failed: {Failure}", at, Reason(ex));
                retryAfter = TimeSpan.Zero;
                continue;
            }
            _resumed = answer;
            int status = (int)answer.StatusCode;
            if (answer.StatusCode == HttpStatusCode.PartialContent)
            {
                if (Mismatch(answer) is { } mismatch)
                {
                    throw GiveUp(failure, at, mismatch);
                }
                _logger.LogInformation("The server {Failure} at byte {Offset}; resumed", failure.Message, at);
                return await answer.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            // Busy or failing for a moment (a 509 is a host at its bandwidth limit); a refusal of the headers has had
            // its renewal already, and anything else is no longer this file at this byte.
            if (status is (int)HttpStatusCode.TooManyRequests or >= 500)
            {
                _logger.LogDebug("Resuming at byte {Offset} failed: the server answered {Status}", at, status);
                retryAfter = RetryAfter(answer);
                continue;
            }
            throw GiveUp(failure, at, string.Create(CultureInfo.InvariantCulture, $"answered {status}"));
        }
    }

    public void Dispose() => _resumed?.Dispose();

    // The answer runs to the file's end: the resume asks the rest, as the player asked it.
    private bool RunsToTheEnd => _total is { } total && _end == total - 1;

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
        bool otherTag = _etag is not null && answer.Headers.ETag is { } etag && !etag.Equals(_etag);
        bool otherDate = _lastModified is { } before && content.LastModified is { } now && now != before;
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

    private static bool IsBytes(ContentRangeHeaderValue range) => range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase);

    // What went wrong, never the message of a request's failure: some carry the address.
    private static string Reason(Exception ex) => ex is MediaUpstreamException upstream ? upstream.Message : MediaRoute.Kind(ex);
}
