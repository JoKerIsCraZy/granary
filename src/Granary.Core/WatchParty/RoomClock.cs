namespace Granary.Core.WatchParty;

/// <summary>
/// The room's estimate of the server's clock from ping and pong, NTP style, as Silo's apps and web keep it: the offset
/// of the sample with the shortest round trip among the last eight, since waiting in queues only ever lengthens one. A
/// sample that cannot agree with the best (each bounds the offset to half its round trip) means a clock stepped, and
/// the older samples go. Commands run at their <c>execute_at</c> on this clock, never on the device's own.
/// </summary>
/// <remarks>Used on one thread at a time (the room socket's).</remarks>
public sealed class RoomClock
{
    /// <summary>Pings come this often until a connection has <see cref="FastSamples"/> samples, then every <see cref="SlowPing"/>.</summary>
    public static readonly TimeSpan FastPing = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan SlowPing = TimeSpan.FromSeconds(15);

    public const int FastSamples = 4;

    private const int MaxSamples = 8;
    private const double MaxRttMs = 5_000;
    private const double TruncationMs = 2;
    private const double StepToleranceMs = 50;

    private readonly List<(double OffsetMs, double RttMs)> _samples = [];
    private readonly Lock _gate = new();

    /// <summary>Server time minus local time in milliseconds; null before a usable sample.</summary>
    public double? OffsetMs
    {
        get
        {
            lock (_gate)
            {
                return Best()?.OffsetMs;
            }
        }
    }

    /// <summary>Takes one sample; false when it was unusable (a negative or five-second round trip).</summary>
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

    /// <summary>The server's clock now, by this estimate; null before one.</summary>
    public DateTimeOffset? ServerNow(DateTimeOffset localNow) => OffsetMs is { } offset ? localNow.AddMilliseconds(offset) : null;

    // Newest first, so the newest of equal round trips wins.
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
