using System.Net;
using Granary.Core.Playback;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>
/// A probe body on a manual clock: each chunk arrives at its time after the first byte, reads moving the clock there,
/// so the probe's ticker closes the windows on the way. After the last chunk the body ends at its end time, or stalls
/// with the clock running on until the reader gives up.
/// </summary>
internal sealed class TimedProbeStream(ManualTimeProvider time) : Stream
{
    private readonly Queue<(long At, long Bytes)> _chunks = new();
    private DateTimeOffset _origin;
    private bool _started;
    private long _left;

    /// <summary>How long the first byte takes after the read for it; the clock moves that far first.</summary>
    public TimeSpan FirstByteAfter { get; init; }

    public long? EndMs { get; init; }

    public int Reads { get; private set; }

    public static TimedProbeStream Of(ManualTimeProvider time, ProbeCase probe)
    {
        var stream = new TimedProbeStream(time) { EndMs = probe.End };
        foreach (var chunk in probe.Chunks)
        {
            stream.Chunk(chunk[0], chunk[1]);
        }
        return stream;
    }

    /// <summary>Steady arrivals: <paramref name="kbps"/> in a chunk every 100 ms from the first byte to <paramref name="untilMs"/>.</summary>
    public static TimedProbeStream Steady(ManualTimeProvider time, long kbps, long untilMs, long? endMs = null)
    {
        var stream = new TimedProbeStream(time) { EndMs = endMs };
        for (long at = 0; at < untilMs; at += 100)
        {
            stream.Chunk(at, kbps * 100 / 8);
        }
        return stream;
    }

    public TimedProbeStream Chunk(long atMs, long bytes)
    {
        _chunks.Enqueue((atMs, bytes));
        return this;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        Reads++;
        if (!_started)
        {
            if (FirstByteAfter > TimeSpan.Zero)
            {
                time.Advance(FirstByteAfter);
                cancellationToken.ThrowIfCancellationRequested();
            }
            _origin = time.GetUtcNow();
            _started = true;
        }
        if (_left == 0 && _chunks.TryDequeue(out var chunk))
        {
            MoveTo(chunk.At);
            cancellationToken.ThrowIfCancellationRequested();
            _left = chunk.Bytes;
        }
        if (_left > 0)
        {
            int count = (int)Math.Min(_left, buffer.Length);
            _left -= count;
            return count;
        }
        if (EndMs is { } end)
        {
            MoveTo(end);
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }
        // Stalled: the clock runs on until the reader gives up.
        for (int step = 0; step < 400 && !cancellationToken.IsCancellationRequested; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        throw new OperationCanceledException(cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    private void MoveTo(long atMs)
    {
        var target = _origin + TimeSpan.FromMilliseconds(atMs);
        var now = time.GetUtcNow();
        if (target > now)
        {
            time.Advance(target - now);
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Hands out clients over one handler, and remembers the names asked for.</summary>
internal sealed class FakeHttpClients(HttpMessageHandler handler) : IHttpClientFactory
{
    public List<string> Names { get; } = [];

    public HttpClient CreateClient(string name)
    {
        Names.Add(name);
        return new HttpClient(handler, disposeHandler: false);
    }
}

internal sealed class FakeNetworkLink : INetworkLink
{
    public NetworkLink Current { get; set; } = new(NetworkTransports.Ethernet, false);
}

internal static class ProbeAnswers
{
    public static Func<Task<HttpResponseMessage>> Body(Stream body) =>
        () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });

    public static Func<Task<HttpResponseMessage>> Redirect(string location, HttpStatusCode status = HttpStatusCode.TemporaryRedirect) => () =>
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return Task.FromResult(response);
    };

    public static Func<Task<HttpResponseMessage>> RateLimited(TimeSpan? retryAfter) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        }
        return Task.FromResult(response);
    };
}
