namespace Granary.Core.WatchParty;

/// <summary>
/// The room's estimate of the server's clock from ping and pong, NTP style, as Silo's apps and web keep it: queueing
/// only ever lengthens a round trip, so the estimate is the median offset of the three samples with the shortest round
/// trips among the last eight, and none before <see cref="MinSamples"/> samples (one lucky sample does not decide it).
/// A sample that cannot agree with the best (each bounds the offset to half its round trip) means a clock stepped, and
/// the older samples go; one that puts the clocks more than <see cref="MaxOffset"/> apart is no sample. Commands run at
/// their <c>execute_at</c> on this clock, never on the device's own.
/// </summary>
/// <remarks>Recorded on the room socket's thread and read on the UI thread.</remarks>
public sealed class RoomClock
{
    /// <summary>Pings come this often until a connection has <see cref="FastSamples"/> samples, then every <see cref="SlowPing"/>.</summary>
    public static readonly TimeSpan FastPing = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan SlowPing = TimeSpan.FromSeconds(15);

    /// <summary>A server clock further off than this is a broken sample, not a clock.</summary>
    public static readonly TimeSpan MaxOffset = TimeSpan.FromHours(12);

    public const int FastSamples = 4;

    /// <summary>Samples taken before the offset is known.</summary>
    public const int MinSamples = 3;

    private const int MaxSamples = 8;
    private const int BestSamples = 3;
    private const double MaxRttMs = 5_000;
    private const double TruncationMs = 2;
    private const double StepToleranceMs = 50;

    private readonly List<(double OffsetMs, double RttMs)> _samples = [];
    private readonly Lock _gate = new();

    /// <summary>Server time minus local time in milliseconds; null before <see cref="MinSamples"/> usable samples.</summary>
    public double? OffsetMs
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count < MinSamples)
                {
                    return null;
                }
                // The shortest round trips, newest first among equals; the median of their offsets.
                var best = _samples.Select((sample, index) => (sample, index))
                    .OrderBy(s => s.sample.RttMs).ThenByDescending(s => s.index)
                    .Take(BestSamples).Select(s => s.sample.OffsetMs).Order().ToList();
                return best[best.Count / 2];
            }
        }
    }

    /// <summary>Takes one sample; false when it was unusable (a negative or five-second round trip, a clock half a day off).</summary>
    public bool Record(DateTimeOffset clientSent, DateTimeOffset serverReceived, DateTimeOffset serverSent, DateTimeOffset clientReceived)
    {
        double hold = (serverSent - serverReceived).TotalMilliseconds;
        double rtt = (clientReceived - clientSent).TotalMilliseconds - hold;
        if (hold < 0 || rtt < -TruncationMs || rtt > MaxRttMs)
        {
            return false;
        }
        rtt = Math.Max(0, rtt);
        double offset = ((serverReceived - clientSent).TotalMilliseconds + (serverSent - clientReceived).TotalMilliseconds) / 2;
        if (!double.IsFinite(offset) || Math.Abs(offset) > MaxOffset.TotalMilliseconds)
        {
            return false;
        }
        lock (_gate)
        {
            if (Best() is { } best && Math.Abs(offset - best.OffsetMs) * 2 > rtt + best.RttMs + 2 * StepToleranceMs)
            {
                _samples.Clear();
            }
            _samples.Add((offset, rtt));
            if (_samples.Count > MaxSamples)
            {
                _samples.RemoveAt(0);
            }
        }
        return true;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
        }
    }

    // The shortest round trip, the newest of equals: what a new sample has to agree with.
    private (double OffsetMs, double RttMs)? Best()
    {
        (double OffsetMs, double RttMs)? best = null;
        for (int i = _samples.Count - 1; i >= 0; i--)
        {
            if (best is null || _samples[i].RttMs < best.Value.RttMs)
            {
                best = _samples[i];
            }
        }
        return best;
    }
}
