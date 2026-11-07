using Granary.Core.Navigation;

namespace Granary.Tests.Navigation;

/// <summary>
/// The focus a start places quietly on its first page: the remote's first arrow or OK shows where it is and moves or
/// opens nothing; a pointer press or focus anywhere else ends that.
/// </summary>
public sealed class QuietStartFocusTests
{
    private readonly object _card = new();
    private readonly object _other = new();
    private readonly QuietStartFocus _start = new();
    private int _shows;
    private bool _showWorks = true;

    // A key, as the window asks: true when the quiet focus took it.
    private bool Key(object? focused, bool isArrowOrOk = true, bool isShown = false) =>
        _start.ShowsInPlace(isArrowOrOk, focused, isShown, () =>
        {
            _shows++;
            return _showWorks;
        });

    [Fact]
    public void TheFirstArrowOrOk_OnTheQuietFocus_OnlyShowsIt()
    {
        _start.Place(_card);

        Assert.True(Key(_card));
        Assert.Equal(1, _shows);
    }

    [Fact]
    public void OnlyTheFirst_TheNextArrowMoves()
    {
        _start.Place(_card);
        Key(_card);

        Assert.False(Key(_card));
        Assert.Equal(1, _shows);
    }

    [Fact]
    public void WithNothingPlaced_EveryArrowMoves()
    {
        Assert.False(Key(_card));
        Assert.Equal(0, _shows);
    }

    [Fact]
    public void WithNothingPlaced_OtherKeysPass()
    {
        Assert.False(Key(_card, isArrowOrOk: false));
        Assert.Equal(0, _shows);
    }

    [Fact]
    public void AFocusShownAlready_Moves_AndTheQuietFocusEnds()
    {
        _start.Place(_card);

        Assert.False(Key(_card, isShown: true));
        Assert.False(Key(_card));
        Assert.Equal(0, _shows);
    }

    [Fact]
    public void FocusOnAnotherElement_Moves_AndTheQuietFocusEnds()
    {
        _start.Place(_card);

        Assert.False(Key(_other));
        Assert.False(Key(_card));
        Assert.Equal(0, _shows);
    }

    [Fact]
    public void WithNoFocusAtAll_TheArrowPasses()
    {
        _start.Place(_card);

        Assert.False(Key(null));
    }

    [Fact]
    public void OtherKeys_Pass_AndLeaveItWaiting()
    {
        _start.Place(_card);

        Assert.False(Key(_card, isArrowOrOk: false));
        Assert.True(Key(_card));
    }

    [Fact]
    public void AShowThatFails_PassesTheKey_AndLeavesItWaiting()
    {
        _start.Place(_card);
        _showWorks = false;

        Assert.False(Key(_card));

        _showWorks = true;
        Assert.True(Key(_card));
    }

    [Fact]
    public void FocusArrivingOnThePlacedElement_KeepsIt()
    {
        // The window hears of the placement itself a moment later.
        _start.Place(_card);
        _start.FocusMoved(_card);

        Assert.True(Key(_card));
    }

    [Fact]
    public void FocusMovingAnywhereElse_EndsIt_EvenWhenItComesBack()
    {
        _start.Place(_card);
        _start.FocusMoved(_other);
        _start.FocusMoved(_card);

        Assert.False(Key(_card));
    }

    [Fact]
    public void APointerPress_EndsIt()
    {
        _start.Place(_card);
        _start.End();

        Assert.False(Key(_card));
    }

    [Fact]
    public void PlacedAgain_FollowsTheNewElement()
    {
        // The page took a moment to show its cards: the start placed focus again.
        _start.Place(_other);
        _start.FocusMoved(_card);
        _start.Place(_card);

        Assert.True(Key(_card));
    }
}
