using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>The speed test's arithmetic against the server's oracle, and the edges it leaves to the client.</summary>
public sealed class SpeedTestMeterTests
{
    [Theory]
    [MemberData(nameof(ProbeVectors.Cases), MemberType = typeof(ProbeVectors))]
    public void TheMeter_MeasuresAsTheServersCasesSay(string name, ProbeCase probe)
    {
        var meter = new SpeedTestMeter(probe.Mode, probe.TargetKbps);

        foreach (var chunk in probe.Chunks)
        {
            if (probe.End is { } end && chunk[0] > end)
            {
                break;
            }
            meter.OnBytes(chunk[0], chunk[1]);
        }
        if (probe.End is { } ended)
        {
            meter.OnEnd(ended);
        }
        else
        {
            meter.AdvanceTo(probe.Mode.MaxMs);
        }

        // The name rides along, so a failure says which case.
        Assert.Equal((name, probe.Expect), (name, meter.Result()));
        if (probe.Stop is { } stop)
        {
            Assert.Equal((name, (long?)stop), (name, meter.StopAtMs));
        }
    }

    [Fact]
    public void TheAccurateTest_IgnoresTheTarget()
    {
        var meter = new SpeedTestMeter(SpeedTestMode.Accurate, 1000);

        meter.OnBytes(0, 1_000_000);
        meter.OnBytes(600, 1_000_000);
        meter.AdvanceTo(1000);

        Assert.False(meter.IsStopped);
    }

    [Fact]
    public void ARateUnderTheFloor_MeasuredNothing()
    {
        var meter = new SpeedTestMeter(SpeedTestMode.Accurate, null);

        meter.OnBytes(0, 10);
        meter.OnBytes(700, 5);
        meter.AdvanceTo(SpeedTestMode.Accurate.MaxMs);

        Assert.Equal(SpeedTestMode.Accurate.MaxMs, meter.StopAtMs);
        Assert.Null(meter.Result());
    }

    [Fact]
    public void BytesAfterTheStop_DoNotCount()
    {
        var meter = new SpeedTestMeter(SpeedTestMode.Accurate, null);
        meter.OnBytes(0, 1_000_000);
        for (long at = 600; at < 10_000; at += 500)
        {
            meter.OnBytes(at, 1_000_000);
        }

        meter.OnBytes(10_200, 999_999_999);

        Assert.Equal(10_000, meter.StopAtMs);
        Assert.Equal(new SpeedTestResult(16_000, false, 6_000_000, 3000), meter.Result());
    }
}
