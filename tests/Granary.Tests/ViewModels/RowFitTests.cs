using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>When the minimized player's end time shows after its times: while the row fits, without blinking at the edge.</summary>
public sealed class RowFitTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARowWithRoomToSpare_ShowsAll(bool shownBefore) => Assert.True(RowFit.ShowsAll(100, 120, shownBefore));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARowTooWide_LeavesTheRestOut(bool shownBefore) => Assert.False(RowFit.ShowsAll(130, 120, shownBefore));

    [Fact]
    public void HalfAPixelOfRounding_LeavesNothingOut() => Assert.True(RowFit.ShowsAll(120.4, 120, shownBefore: true));

    [Fact]
    public void LeftOut_TheRestComesBack_OnlyWithRoomToSpare()
    {
        Assert.False(RowFit.ShowsAll(120 - RowFit.ComebackMargin + 1, 120, shownBefore: false));
        Assert.True(RowFit.ShowsAll(120 - RowFit.ComebackMargin, 120, shownBefore: false));
    }

    [Fact]
    public void ATimeThatGrowsByADigitAtTheEdge_LeavesTheRestOut_WithoutBlinking()
    {
        // "-10:00" no longer fits, then "-9:59" is a digit narrower again.
        bool shows = RowFit.ShowsAll(121, 120, shownBefore: true);
        Assert.False(shows);

        Assert.False(RowFit.ShowsAll(121 - 7, 120, shows));
    }

    [Fact]
    public void EndlessRoom_ShowsAll() => Assert.True(RowFit.ShowsAll(5000, double.PositiveInfinity, shownBefore: false));
}
