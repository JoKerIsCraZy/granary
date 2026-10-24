namespace Granary.Tests.Fakes;

/// <summary>A clock the test sets. Timers still run on real time.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
