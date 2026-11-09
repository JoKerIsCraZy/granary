using Granary.Core.ViewModels;

namespace Granary.Tests.ViewModels;

/// <summary>The main window opens where it was closed, on a display as the displays are now.</summary>
public sealed class WindowPlaceTests
{
    private static readonly WindowBounds Primary = new(0, 0, 1920, 1040);
    private static readonly WindowBounds LeftOfIt = new(-1920, 322, 1920, 1032);
    private static readonly WindowBounds RightOfIt = new(1920, 0, 1920, 1040);

    private static WindowBounds Opened(WindowPlace place, double scale, params WindowBounds[] workAreas) =>
        place.OpenOn(workAreas, workAreas[0], scale);

    [Fact]
    public void OnTheSameDisplay_TheWindowOpensWhereItWasClosed()
    {
        var place = new WindowPlace(new WindowBounds(312, 140, 1440, 900), 1, IsMaximized: false);

        Assert.Equal(place.Bounds, Opened(place, 1, Primary));
    }

    [Fact]
    public void OnADisplayLeftOfThePrimary_ItOpensThereToo()
    {
        var place = new WindowPlace(new WindowBounds(-1700, 400, 1440, 800), 1, IsMaximized: false);

        Assert.Equal(place.Bounds, Opened(place, 1, LeftOfIt, Primary));
    }

    [Fact]
    public void SpanningTwoDisplays_ItKeepsItsPlaceAndSize()
    {
        var place = new WindowBounds(1000, 100, 3000, 900);

        Assert.Equal(place, Opened(new WindowPlace(place, 1, IsMaximized: false), 1, Primary, RightOfIt));
    }

    [Fact]
    public void ItsDisplayGone_ItOpensOnTheNearestOne()
    {
        var place = new WindowPlace(new WindowBounds(2400, 300, 1440, 900), 1, IsMaximized: false);

        Assert.Equal(new WindowBounds(480, 140, 1440, 900), Opened(place, 1, Primary));
    }

    [Fact]
    public void ItsDisplayGone_ATooLargeWindow_ShrinksToTheNearestOne()
    {
        var place = new WindowPlace(new WindowBounds(4000, 0, 3000, 1500), 1, IsMaximized: false);

        Assert.Equal(Primary, Opened(place, 1, Primary));
    }

    [Fact]
    public void ClosedAt150_OpenedAt100_ItKeepsItsSizeInViewPixels()
    {
        // 1600 × 1000 view pixels at 150 %: 2400 × 1500 pixels, larger than this display; at 100 % they fit.
        var place = new WindowPlace(new WindowBounds(200, 100, 2400, 1500), 1.5, IsMaximized: false);

        Assert.Equal(new WindowBounds(200, 100, 1600, 1000), Opened(place, 1, Primary));
    }

    [Fact]
    public void ClosedAt100_OpenedAt150_ItGrowsWithTheScale_WhileItFits()
    {
        var place = new WindowBounds(100, 50, 1000, 600);

        Assert.Equal(new WindowBounds(100, 50, 1500, 900), Opened(new WindowPlace(place, 1, IsMaximized: false), 1.5, Primary));
    }

    [Fact]
    public void ClosedAt100_OpenedAt150_AWindowTooLargeThen_FitsTheDisplay()
    {
        // 1800 × 1000 at 100 % would be 2700 × 1500 at 150 %, more than the display has.
        var place = new WindowPlace(new WindowBounds(100, 50, 1800, 1000), 1, IsMaximized: false);

        Assert.Equal(Primary, Opened(place, 1.5, Primary));
    }

    [Fact]
    public void OnADisplayOfAnotherScale_ItsTitleBarCountsAtThatScale()
    {
        // 200 pixels of the title bar show: enough at 100 % (120), too few at 200 % (240).
        var bounds = new WindowBounds(1720, 100, 800, 500);

        Assert.Equal(bounds, Opened(new WindowPlace(bounds, 1, IsMaximized: false), 1, Primary));
        var doubled = new WindowPlace(new WindowBounds(1720, 100, 400, 250), 1, IsMaximized: false);
        Assert.Equal(new WindowBounds(1120, 100, 800, 500), Opened(doubled, 2, Primary));
    }

    [Fact]
    public void ARestoredWindow_IsKeptAsItIs()
    {
        var keeper = new WindowPlaceKeeper();

        keeper.Keep(WindowState.Restored, new WindowBounds(312, 140, 1440, 900));

        Assert.Equal(new WindowPlace(new WindowBounds(312, 140, 1440, 900), 1.25, IsMaximized: false), keeper.ToPlace(1.25));
    }

    [Fact]
    public void AMaximizedWindow_KeepsThePlaceItHadBefore()
    {
        var keeper = new WindowPlaceKeeper();
        keeper.Keep(WindowState.Restored, new WindowBounds(312, 140, 1440, 900));

        keeper.Keep(WindowState.Maximized, new WindowBounds(-7, -7, 1934, 1054));

        Assert.Equal(new WindowPlace(new WindowBounds(312, 140, 1440, 900), 1, IsMaximized: true), keeper.ToPlace(1));
    }

    [Fact]
    public void AMinimizedWindow_KeepsTheLastPlaceItReallyHad()
    {
        var keeper = new WindowPlaceKeeper();
        keeper.Keep(WindowState.Restored, new WindowBounds(312, 140, 1440, 900));
        keeper.Keep(WindowState.Maximized, new WindowBounds(-7, -7, 1934, 1054));

        keeper.Keep(WindowState.Minimized, new WindowBounds(-32000, -32000, 160, 28));

        Assert.Equal(new WindowPlace(new WindowBounds(312, 140, 1440, 900), 1, IsMaximized: true), keeper.ToPlace(1));
    }

    [Fact]
    public void AWindowRestoredFromMaximized_IsNoLongerMaximized()
    {
        var keeper = new WindowPlaceKeeper();
        keeper.Keep(WindowState.Restored, new WindowBounds(312, 140, 1440, 900));
        keeper.Keep(WindowState.Maximized, new WindowBounds(-7, -7, 1934, 1054));

        keeper.Keep(WindowState.Restored, new WindowBounds(312, 140, 1440, 900));

        Assert.False(keeper.ToPlace(1)!.IsMaximized);
    }

    [Fact]
    public void AWindowThatNeverHadAPlaceOfItsOwn_HasNoneToSave()
    {
        var keeper = new WindowPlaceKeeper();

        keeper.Keep(WindowState.Maximized, new WindowBounds(-7, -7, 1934, 1054));
        keeper.Keep(WindowState.Minimized, new WindowBounds(-32000, -32000, 160, 28));

        Assert.Null(keeper.ToPlace(1));
    }
}
