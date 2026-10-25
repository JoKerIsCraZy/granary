namespace Granary.Core.Playback;

/// <summary>The two connection speed tests: how much one asks for, how long it reads, and the windows its rate settles over.</summary>
public sealed record SpeedTestMode(long RequestBytes, long MaxMs, int SettleWindows)
{
    /// <summary>Automatic: stops once a window proves the server's target.</summary>
    public static SpeedTestMode Quick { get; } = new(100_000_000, 3_000, 2);

    /// <summary><b>Test speed</b> only: the rate held over the final three seconds.</summary>
    public static SpeedTestMode Accurate { get; } = new(1_000_000_000, 10_000, 6);
}

/// <summary>What a speed test measured: <see cref="Kbps"/> from <see cref="Bytes"/> over <see cref="DurationMs"/>.</summary>
/// <param name="LowerBound">The link carries at least <see cref="Kbps"/>: a window reached the target, the rate passed the
/// ceiling, or it is a whole-transfer average.</param>
public sealed record SpeedTestResult(long Kbps, bool LowerBound, long Bytes, int DurationMs);

/// <summary>
/// The connection speed test's arithmetic, apart from any I/O, as the server's buffering-quality design has it
/// (Connection speed test → Measuring) and Android's SpeedTestMeter counts it; the design's <c>probe_cases</c> pin it.
/// Times are milliseconds after the first byte of the body. Bytes count in the 500 ms window they arrive in; window 0,
/// TCP ramp-up, never counts. <see cref="AdvanceTo"/> closes windows on the clock, so one with no bytes counts as 0.
/// Not thread-safe: the caller serializes the reader and the ticker.
/// </summary>
public sealed class SpeedTestMeter
{
    public const long WindowMs = 500;
    public const long MinKbps = 100;
    public const long MaxKbps = 1_000_000;

    /// <summary>A test whose first byte has not arrived by then, the request and a redirect included, is abandoned.</summary>
    public static readonly TimeSpan FirstByteDeadline = TimeSpan.FromSeconds(10);

    private readonly SpeedTestMode _mode;
    private readonly long? _target;

    // Bytes of each window closed so far, by window number.
    private readonly List<long> _closed = [];
    private long _openBytes;
    private long _totalBytes;

    // When the transfer ended, if it ended before the test stopped.
    private long? _endMs;

    // Bytes of the window that reached the target, once one has.
    private long? _targetWindowBytes;

    /// <param name="targetKbps"><c>bandwidth_probe_target_kbps</c>; only the quick test stops at it.</param>
    public SpeedTestMeter(SpeedTestMode mode, long? targetKbps)
    {
        ArgumentNullException.ThrowIfNull(mode);
        _mode = mode;
        _target = mode == SpeedTestMode.Quick ? targetKbps : null;
    }

    /// <summary>When the test stops reading, once it has decided to.</summary>
    public long? StopAtMs { get; private set; }

    public bool IsStopped => StopAtMs is not null;

    /// <summary>
    /// Closes every window that ended by <paramref name="nowMs"/>. The quick test stops at the end of the first window
    /// after window 0 that reaches the target; either test stops at its limit.
    /// </summary>
    public void AdvanceTo(long nowMs)
    {
        while (!IsStopped && (_closed.Count + 1) * WindowMs <= nowMs)
        {
            long bytes = _openBytes;
            _closed.Add(bytes);
            _openBytes = 0;
            long windowEndMs = _closed.Count * WindowMs;
            if (_closed.Count > 1 && _target is { } target && RateKbps(bytes, 1) >= target)
            {
                _targetWindowBytes = bytes;
                StopAtMs = windowEndMs;
            }
            else if (windowEndMs >= _mode.MaxMs)
            {
                StopAtMs = _mode.MaxMs;
            }
        }
    }

    /// <summary><paramref name="bytes"/> arrived at <paramref name="atMs"/>; after the test stopped they no longer count.</summary>
    public void OnBytes(long atMs, long bytes)
    {
        AdvanceTo(atMs);
        if (IsStopped)
        {
            return;
        }
        _openBytes += bytes;
        _totalBytes += bytes;
    }

    /// <summary>The body ended cleanly at <paramref name="atMs"/>; a window it cut short does not count.</summary>
    public void OnEnd(long atMs)
    {
        AdvanceTo(atMs);
        if (IsStopped)
        {
            return;
        }
        StopAtMs = atMs;
        _endMs = atMs;
    }

    /// <summary>
    /// The target as a lower bound when a window reached it, else the settled rate over the last complete windows after
    /// window 0, else the whole-transfer average. Null when the test measured nothing.
    /// </summary>
    public SpeedTestResult? Result()
    {
        if (_targetWindowBytes is { } targetBytes)
        {
            long kbps = _target!.Value;
            return kbps < MinKbps ? null : new SpeedTestResult(Math.Min(kbps, MaxKbps), true, targetBytes, (int)WindowMs);
        }
        var counted = _closed.Skip(1).TakeLast(_mode.SettleWindows).ToList();
        if (counted.Count == 0)
        {
            return WholeTransfer();
        }
        long bytes = counted.Sum();
        long rate = RateKbps(bytes, counted.Count);
        int durationMs = (int)(counted.Count * WindowMs);
        return rate switch
        {
            < MinKbps => null,
            > MaxKbps => new SpeedTestResult(MaxKbps, true, bytes, durationMs),
            _ => new SpeedTestResult(rate, false, bytes, durationMs),
        };
    }

    // Every byte asked for arrived before a window after window 0 completed: the average from the first byte to the
    // last, ramp included, so it can only read low and is a lower bound.
    private SpeedTestResult? WholeTransfer()
    {
        if (_endMs is not { } end || _totalBytes < _mode.RequestBytes)
        {
            return null;
        }
        long average = _totalBytes * 8 / Math.Max(end, 1);
        long kbps = Math.Min(_target is { } target && average >= target ? target : average, MaxKbps);
        return kbps < MinKbps ? null : new SpeedTestResult(kbps, true, _totalBytes, (int)end);
    }

    // Bits per millisecond are kbit/s.
    private static long RateKbps(long bytes, int windows) => bytes * 8 / (windows * WindowMs);
}
