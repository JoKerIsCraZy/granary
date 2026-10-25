using Granary.Core.Api.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

public enum ProbeReadingKind
{
    Measured,

    /// <summary>The server allows another probe after <see cref="ProbeReading.RetryAfterSeconds"/>.</summary>
    RateLimited,

    /// <summary>The server refused the download; the test never falls back to measuring another path.</summary>
    Unavailable,

    /// <summary>No complete window after the ramp-up and a transfer short of what was asked, or under 100 kbps.</summary>
    TooLittleData,

    /// <summary>The request or the transfer failed, or no byte arrived in time.</summary>
    NetworkError,
}

public sealed record ProbeReading(ProbeReadingKind Kind, SpeedTestResult? Result = null, int RetryAfterSeconds = 0);

/// <summary>
/// Times one speed test download, as Android's BandwidthProbe does: the clock starts at the first byte of the body, a
/// ticker closes each 500 ms window on the clock while reads take bytes as they come, so a silent window still counts,
/// and the transfer is closed once the meter stops. A test without its first byte within 10 s of asking is abandoned.
/// </summary>
public sealed class BandwidthProbe(BandwidthProbeApi api, TimeProvider time, ILogger<BandwidthProbe> logger)
{
    private const int ReadBufferBytes = 64 * 1024;

    public async Task<ProbeReading> MeasureAsync(Uri server, Uri url, SpeedTestMode mode, long? targetKbps, CancellationToken cancellationToken)
    {
        // The deadline covers the request and a redirect as well as the body: a server that never answers is abandoned
        // the same way as one that never sends.
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var firstByte = new FirstByteWatch(waiting);
        using var deadline = time.CreateTimer(_ => firstByte.Miss(), null, SpeedTestMeter.FirstByteDeadline, Timeout.InfiniteTimeSpan);
        try
        {
            using var answer = await api.OpenAsync(server, url, waiting.Token).ConfigureAwait(false);
            switch (answer.Kind)
            {
                case BandwidthProbeAnswerKind.RateLimited:
                    return new ProbeReading(ProbeReadingKind.RateLimited, RetryAfterSeconds: answer.RetryAfterSeconds);
                case BandwidthProbeAnswerKind.Unavailable:
                    logger.LogInformation("The speed test's probe answered {Status}", answer.Status);
                    return new ProbeReading(ProbeReadingKind.Unavailable);
            }
            await using var body = await answer.ReadBodyAsync(waiting.Token).ConfigureAwait(false);
            var meter = new SpeedTestMeter(mode, targetKbps);
            await TimeAsync(body, meter, waiting.Token, firstByte, cancellationToken).ConfigureAwait(false);
            return meter.Result() is { } result ? new ProbeReading(ProbeReadingKind.Measured, result) : new ProbeReading(ProbeReadingKind.TooLittleData);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
        {
            logger.LogInformation("The speed test's download failed: {Failure}", firstByte.Missed ? "no byte in time" : ex.GetType().Name);
            return new ProbeReading(ProbeReadingKind.NetworkError);
        }
    }

    private async Task TimeAsync(Stream body, SpeedTestMeter meter, CancellationToken waiting, FirstByteWatch firstByte, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferBytes];
        int first = await body.ReadAsync(buffer, waiting).ConfigureAwait(false);
        if (first == 0 || !firstByte.Arrived())
        {
            return;
        }
        long origin = time.GetTimestamp();
        long Elapsed() => (long)time.GetElapsedTime(origin).TotalMilliseconds;
        var gate = new Lock();
        meter.OnBytes(0, first);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var window = TimeSpan.FromMilliseconds(SpeedTestMeter.WindowMs);
        // Disposed before the source it cancels.
        using var ticker = time.CreateTimer(_ =>
        {
            lock (gate)
            {
                meter.AdvanceTo(Elapsed());
                if (!meter.IsStopped)
                {
                    return;
                }
            }
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The read ended meanwhile.
            }
        }, null, window, window);
        try
        {
            while (true)
            {
                int count = await body.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                long at = Elapsed();
                lock (gate)
                {
                    if (count == 0)
                    {
                        meter.OnEnd(at);
                    }
                    else
                    {
                        meter.OnBytes(at, count);
                    }
                    if (meter.IsStopped)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is OperationCanceledException or IOException && StoppedUnder(gate, meter))
        {
            // The meter stopped the read: what it measured stands.
        }
    }

    private static bool StoppedUnder(Lock gate, SpeedTestMeter meter)
    {
        lock (gate)
        {
            return meter.IsStopped;
        }
    }

    // The first byte and the deadline race: whichever comes first wins.
    private sealed class FirstByteWatch(CancellationTokenSource waiting)
    {
        private readonly Lock _gate = new();
        private bool _arrived;

        public bool Missed { get; private set; }

        public void Miss()
        {
            lock (_gate)
            {
                if (_arrived)
                {
                    return;
                }
                Missed = true;
            }
            try
            {
                waiting.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The test is over.
            }
        }

        /// <summary>False when the deadline came first.</summary>
        public bool Arrived()
        {
            lock (_gate)
            {
                _arrived = !Missed;
                return _arrived;
            }
        }
    }
}
