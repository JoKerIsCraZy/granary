namespace Granary.Core.Remote;

/// <summary>
/// One phone's command budget, as silo-roku's <c>policy.bs</c> <c>allow</c> keeps it (tvOS's budget): a token bucket of 40
/// that fills by 20 a second, and a rate-limit error at most once a second. <see cref="ForSignals"/> makes the smaller
/// budget a phone's <c>hello</c>, <c>presence</c> and <c>handoff_cancel</c> take from instead (Granary's own).
/// </summary>
/// <param name="time">The clock the bucket fills on.</param>
/// <param name="perSecond">How many it fills by each second.</param>
/// <param name="burst">How many it holds.</param>
public sealed class CommandBudget(TimeProvider time, int perSecond, int burst)
{
    public const int PerSecond = 20;
    public const int Burst = 40;

    /// <summary>
    /// The signals' budget: a phone sends each of them once, when something changed, and never again, so a burst of
    /// commands before one must not use up what it needs; the few a phone sends get through a budget of their own.
    /// </summary>
    public const int SignalsPerSecond = 5;

    public const int SignalBurst = 10;

    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private double _tokens = burst;
    private long? _takenAt;
    private long? _reportedAt;

    /// <summary>The command budget: <see cref="Burst"/> at once, then <see cref="PerSecond"/> a second.</summary>
    public CommandBudget(TimeProvider time)
        : this(time, PerSecond, Burst)
    {
    }

    /// <summary>The signals' budget: <see cref="SignalBurst"/> at once, then <see cref="SignalsPerSecond"/> a second.</summary>
    public static CommandBudget ForSignals(TimeProvider time) => new(time, SignalsPerSecond, SignalBurst);

    /// <summary>Spends one command; false when the budget is used up.</summary>
    public bool TryTake()
    {
        lock (_gate)
        {
            long now = time.GetTimestamp();
            if (_takenAt is { } before)
            {
                _tokens = Math.Min(burst, _tokens + (time.GetElapsedTime(before, now).TotalSeconds * perSecond));
            }
            _takenAt = now;
            if (_tokens < 1)
            {
                return false;
            }
            _tokens--;
            return true;
        }
    }

    /// <summary>Whether the phone may be told it sends too many commands: once a second at most.</summary>
    public bool MayReport()
    {
        lock (_gate)
        {
            long now = time.GetTimestamp();
            if (_reportedAt is { } before && time.GetElapsedTime(before, now) < ReportEvery)
            {
                return false;
            }
            _reportedAt = now;
            return true;
        }
    }
}
