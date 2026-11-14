using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>
/// A double click on the player's picture is told from two clicks whichever way they reach the page: both through WinUI,
/// or the first through the cursor's shield and the second through WinUI, which then saw only one.
/// </summary>
public sealed class PlayerClicksTests
{
    private static readonly TimeSpan Within = TimeSpan.FromMilliseconds(500);
    private const double Reach = 16;

    private readonly ManualTimeProvider _time = new();
    private readonly PlayerClicks _clicks;

    public PlayerClicksTests() => _clicks = new PlayerClicks(_time);

    [Fact]
    public void AClick_IsNoSecondOne()
    {
        Assert.False(_clicks.IsSecond(100, 100, Within, Reach));
    }

    [Fact]
    public void TwoClicksQuicklyAtOnePlace_AreADoubleClick()
    {
        // The first came through the shield, which left with it: WinUI reports the second as a click of its own.
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(TimeSpan.FromMilliseconds(180));

        Assert.True(_clicks.IsSecond(103, 98, Within, Reach));
    }

    [Fact]
    public void ASecondClickTooLate_IsAFirstOneAgain()
    {
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(Within + TimeSpan.FromMilliseconds(1));

        Assert.False(_clicks.IsSecond(100, 100, Within, Reach));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(_clicks.IsSecond(100, 100, Within, Reach));
    }

    [Fact]
    public void ASecondClickElsewhere_IsAFirstOneAgain()
    {
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.False(_clicks.IsSecond(100 + Reach + 1, 100, Within, Reach));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(_clicks.IsSecond(100 + Reach + 1, 100, Within, Reach));
    }

    [Fact]
    public void AThirdClick_StartsAnew()
    {
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.False(_clicks.IsSecond(100, 100, Within, Reach));
    }

    [Fact]
    public void ADoubleClickTheFrameworkTellsToo_IsTakenOnce()
    {
        // WinUI may report the second click and then the double click: full screen must not go on and off again.
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(_clicks.IsSecond(100, 100, Within, Reach));

        Assert.True(_clicks.TakesDouble(Within));
        Assert.False(_clicks.TakesDouble(Within));
    }

    [Fact]
    public void ADoubleClickOnlyTheFrameworkTells_IsTaken_AndTheNextClickIsAFirstOne()
    {
        // Both clicks went through WinUI, which reports the second as a double click only.
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(_clicks.TakesDouble(Within));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(_clicks.IsSecond(100, 100, Within, Reach));
    }

    [Fact]
    public void TwoDoubleClicksInQuickSuccession_AreBothTaken()
    {
        // Four quick clicks, all through WinUI: into full screen and out again. The second pair is no repeat of the first.
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(_clicks.TakesDouble(Within));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(_clicks.TakesDouble(Within));
    }

    [Fact]
    public void APairToldHere_KeepsBackTheFrameworksReportOfIt_ButNotTheNextPair()
    {
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(_clicks.IsSecond(100, 100, Within, Reach));
        Assert.True(_clicks.TakesDouble(Within));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        // No report of that pair came. The next two clicks go through WinUI, which tells them as a double click.
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(_clicks.TakesDouble(Within));
    }

    [Fact]
    public void TheFrameworksReportOfAPairToldHere_IsKeptBackOnce()
    {
        _clicks.IsSecond(100, 100, Within, Reach);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _clicks.IsSecond(100, 100, Within, Reach);
        _clicks.TakesDouble(Within);

        Assert.False(_clicks.TakesDouble(Within));
        Assert.True(_clicks.TakesDouble(Within));
    }

    [Fact]
    public void TwoDoubleClicksApart_AreBothTaken()
    {
        Assert.True(_clicks.TakesDouble(Within));
        _time.Advance(Within + TimeSpan.FromMilliseconds(1));

        Assert.True(_clicks.TakesDouble(Within));
    }
}
