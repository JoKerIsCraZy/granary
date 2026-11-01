using Granary.Core.ViewModels;

namespace Granary.Tests.ViewModels;

/// <summary>A window coming back from full screen or the mini view lands on a display, whatever happened to its own.</summary>
public sealed class WindowBoundsTests
{
    private const int TitleBarHeight = 32;
    private const int MinimumVisibleWidth = 120;

    private static readonly WindowBounds WorkArea = new(0, 0, 1920, 1040);
    private static readonly WindowBounds RightOfIt = new(1920, 0, 1920, 1040);
    private static readonly WindowBounds LeftOfIt = new(-1920, 0, 1920, 1040);
    private static readonly WindowBounds AboveIt = new(0, -1080, 1920, 1040);

    private static WindowBounds Placed(WindowBounds window, params WindowBounds[] workAreas) =>
        window.OnDisplays(workAreas, workAreas[0], TitleBarHeight, MinimumVisibleWidth);

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
        Assert.Equal(new WindowBounds(-1440, 0, 1440, 900), new WindowBounds(-100, 0, 1440, 900).Within(LeftOfIt));

    [Fact]
    public void AnEmptyWorkArea_ChangesNothing() =>
        Assert.Equal(new WindowBounds(200, 100, 1440, 900), new WindowBounds(200, 100, 1440, 900).Within(new WindowBounds(0, 0, 0, 0)));

    [Fact]
    public void AWindowSpanningTwoDisplays_StaysAsItWas()
    {
        // 3000 px over two displays of 1920: wider than either, and squeezing it onto one would lose the user's layout.
        var spanning = new WindowBounds(1000, 100, 3000, 900);

        Assert.Equal(spanning, Placed(spanning, WorkArea, RightOfIt));
    }

    [Fact]
    public void AWindowOnADisplayLeftOfThePrimary_StaysAsItWas()
    {
        var left = new WindowBounds(-1500, 100, 1440, 900);

        Assert.Equal(left, Placed(left, WorkArea, LeftOfIt));
    }

    [Fact]
    public void AWindowOnADisplayAboveThePrimary_StaysAsItWas()
    {
        var above = new WindowBounds(200, -900, 1440, 800);

        Assert.Equal(above, Placed(above, WorkArea, AboveIt));
    }

    [Fact]
    public void AWindowOnAnUnpluggedDisplay_MovesOntoTheNearest() =>
        Assert.Equal(new WindowBounds(480, 140, 1440, 900), Placed(new WindowBounds(2400, 300, 1440, 900), WorkArea));

    [Fact]
    public void AWindowFlushWithTheEdges_StaysAsItWas_ItsInvisibleFrameReachingPast()
    {
        // Windows 11 draws a window's resize frame invisibly, 7 px past its visible edge at 100 %.
        var flush = new WindowBounds(-7, 0, 974, 1047);

        Assert.Equal(flush, Placed(flush, WorkArea));
    }

    [Fact]
    public void AWindowWhoseTitleBarIsOnTheDisplay_StaysAsItWas_ThoughTheRestHangsBelow()
    {
        var low = new WindowBounds(200, 900, 1440, 900);

        Assert.Equal(low, Placed(low, WorkArea));
    }

    [Fact]
    public void AWindowWhoseTitleBarIsAboveEveryDisplay_ComesDown() =>
        Assert.Equal(new WindowBounds(200, 0, 1440, 900), Placed(new WindowBounds(200, -600, 1440, 900), WorkArea));

    [Fact]
    public void AWindowShowingOnlyASliverOfItsTitleBar_IsPulledIn() =>
        Assert.Equal(new WindowBounds(480, 100, 1440, 900), Placed(new WindowBounds(1880, 100, 1440, 900), WorkArea));

    [Fact]
    public void AWindowTooLargeForTheNearestDisplay_ShrinksOnlyWhenItMustMove() =>
        Assert.Equal(WorkArea, Placed(new WindowBounds(5000, 0, 3000, 1200), WorkArea));
}
