using Granary.Core.Downloads;
using Granary.Tests.Fakes;

namespace Granary.Tests.Downloads;

/// <summary>
/// How fast a transfer goes now: the bytes of the last few seconds over the time they took. None until there is enough to
/// measure, none once the bytes stopped coming, and a measure of its own again after a gap or a part that started over.
/// </summary>
public sealed class TransferRateTests
{
    private const long MB = 1_000_000;

    private static readonly DateTimeOffset Start = ManualTimeProvider.DefaultStart;

    private static DateTimeOffset At(double seconds) => Start + TimeSpan.FromSeconds(seconds);

    // Bytes at a steady pace, a sample each quarter second from `from` seconds on.
    private static void Feed(TransferRate rate, double from, double to, long startBytes, long perSecond)
    {
        for (double t = from; t <= to + 1e-9; t += 0.25)
        {
            rate.Add(At(t), startBytes + (long)((t - from) * perSecond));
        }
    }

    [Fact]
    public void NoSpeed_UntilTwoSecondsOfBytesCame()
    {
        var rate = new TransferRate();
        Feed(rate, 0, 1.75, 0, 4 * MB);

        Assert.Null(rate.BytesPerSecond(At(1.75)));

        rate.Add(At(2), 8 * MB);

        Assert.Equal(4 * MB, rate.BytesPerSecond(At(2))!.Value, 0);
    }

    [Fact]
    public void TheSpeed_IsTheLastFiveSeconds()
    {
        var rate = new TransferRate();
        Feed(rate, 0, 10, 0, 10 * MB);
        Feed(rate, 10.25, 20, 100 * MB + MB / 2, 2 * MB);

        Assert.Equal(2 * MB, rate.BytesPerSecond(At(20))!.Value, tolerance: 1);
    }

    [Fact]
    public void BytesThatStopped_HaveNoSpeedAfterThreeSeconds()
    {
        var rate = new TransferRate();
        Feed(rate, 0, 5, 0, MB);

        Assert.NotNull(rate.BytesPerSecond(At(8)));
        Assert.Null(rate.BytesPerSecond(At(8.25)));
    }

    [Fact]
    public void BytesAfterAGap_AreMeasuredAnew()
    {
        var rate = new TransferRate();
        Feed(rate, 0, 5, 0, MB);

        // Nothing for ten seconds, then quickly on: the gap is no part of the new measure.
        Feed(rate, 15, 16, 5 * MB, 10 * MB);
        Assert.Null(rate.BytesPerSecond(At(16)));

        Feed(rate, 16.25, 17, 15 * MB + 2_500_000, 10 * MB);
        Assert.Equal(10 * MB, rate.BytesPerSecond(At(17))!.Value, tolerance: 1);
    }

    [Fact]
    public void APartThatStartedOver_IsMeasuredAnew()
    {
        var rate = new TransferRate();
        Feed(rate, 0, 5, 50 * MB, MB);

        // The server sent the file from its first byte again.
        Feed(rate, 5.25, 7, 0, 3 * MB);
        Assert.Null(rate.BytesPerSecond(At(7)));

        rate.Add(At(7.25), 6 * MB);
        Assert.Equal(3 * MB, rate.BytesPerSecond(At(7.25))!.Value, tolerance: 1);
    }
}
