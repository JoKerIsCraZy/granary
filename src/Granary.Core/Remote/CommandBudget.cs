namespace Granary.Core.Remote;

/// <summary>
/// One phone's command budget, as silo-roku's <c>policy.bs</c> <c>allow</c> keeps it (tvOS's budget): a token bucket of 40
/// that fills by 20 a second, and a rate-limit error at most once a second.
/// </summary>
public sealed class CommandBudget(TimeProvider time)
{
    public const int PerSecond = 20;
    public const int Burst = 40;

    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private double _tokens = Burst;
    private long? _takenAt;
    private long? _reportedAt;

    /// <summary>Spends one command; false when the budget is used up.</summary>
    public bool TryTake()
    {
        lock (_gate)
        {
            long now = time.GetTimestamp();
            if (_takenAt is { } before)
            {
                _tokens = Math.Min(Burst, _tokens + (time.GetElapsedTime(before, now).TotalSeconds * PerSecond));
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
