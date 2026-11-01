using Granary.Core.ViewModels;

namespace Granary.Tests.ViewModels;

/// <summary>A window coming back from full screen or the mini view lands on a display, whatever happened to its own.</summary>
public sealed class WindowBoundsTests
{
    private static readonly WindowBounds WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void AWindowWithinTheWorkArea_StaysAsItWas() =>
        Assert.Equal(new WindowBounds(200, 100, 1440, 900), new WindowBounds(200, 100, 1440, 900).Within(WorkArea));

    [Fact]
    public void AWindowOnADisplayThatWentAway_MovesOntoThisOne() =>
        Assert.Equal(new WindowBounds(480, 140, 1440, 900), new WindowBounds(2400, 300, 1440, 900).Within(WorkArea));

    [Fact]
    public void AWindowPartlyOff_IsPulledIn() =>
        Assert.Equal(new WindowBounds(0, 0, 1440, 900), new WindowBounds(-300, -50, 1440, 900).Within(WorkArea));

    [Fact]
    public void AWindowLargerThanTheWorkArea_ShrinksToIt() =>
        Assert.Equal(WorkArea, new WindowBounds(-10, -10, 2560, 1440).Within(WorkArea));

    [Fact]
    public void AWorkAreaLeftOfThePrimaryDisplay_IsKeptToo() =>
        Assert.Equal(new WindowBounds(-1440, 0, 1440, 900),
            new WindowBounds(-100, 0, 1440, 900).Within(new WindowBounds(-1920, 0, 1920, 1040)));

    [Fact]
    public void AWindowFlushWithTheEdges_StaysWhereItWas_ItsInvisibleFrameReachingPast()
    {
        // Windows 11 draws a window's resize frame invisibly, 7 px past its visible edge at 100 %.
        var flush = new WindowBounds(-7, 0, 974, 1047);

        Assert.Equal(flush, flush.Within(WorkArea, slack: 16));
    }

    [Fact]
    public void AWindowFurtherOutThanTheSlack_IsStillPulledIn() =>
        Assert.Equal(new WindowBounds(480, 140, 1440, 900), new WindowBounds(2400, 300, 1440, 900).Within(WorkArea, slack: 16));

    [Fact]
    public void AnEmptyWorkArea_ChangesNothing() =>
        Assert.Equal(new WindowBounds(200, 100, 1440, 900), new WindowBounds(200, 100, 1440, 900).Within(new WindowBounds(0, 0, 0, 0)));
}
