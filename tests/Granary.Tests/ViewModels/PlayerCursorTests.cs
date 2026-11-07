using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>
/// The player's cursor follows the mouse only: a real move shows it, it hides with the controls over the picture whatever
/// hid them, and hidden it keeps nothing up.
/// </summary>
public sealed class PlayerCursorTests
{
    private readonly PlayerCursor _cursor = new();

    [Fact]
    public void HidingTheControls_OverThePicture_HidesTheCursor()
    {
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.True(_cursor.IsHidden);
    }

    [Fact]
    public void HidingTheControls_WithThePointerOffThePicture_LeavesTheCursor()
    {
        // The title bar's drag area, where Windows draws the cursor, or another window.
        _cursor.ControlsHidden(isOverPicture: false);

        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void ARealMove_ShowsTheCursor()
    {
        _cursor.Moves(100, 100);
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.True(_cursor.Moves(110, 100));
        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void TheFirstMove_IsARealOne()
    {
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.True(_cursor.Moves(5, 5));
        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void AStillPointerTestedAgain_LeavesTheCursorHidden()
    {
        // A key brings the controls back under the resting pointer, and the framework tests it again where it is.
        _cursor.Moves(100, 100);
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.False(_cursor.Moves(100, 100));
        Assert.True(_cursor.IsHidden);
    }

    [Fact]
    public void Jitter_LeavesTheCursorHidden()
    {
        _cursor.Moves(100, 100);
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.False(_cursor.Moves(101, 101.5));
        Assert.True(_cursor.IsHidden);
    }

    [Fact]
    public void SmallMoves_CountOnceTheyAddUp()
    {
        _cursor.Moves(100, 100);
        _cursor.ControlsHidden(isOverPicture: true);
        _cursor.Moves(101, 100);

        Assert.True(_cursor.Moves(100 + PlayerCursor.MoveThreshold, 100));
        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void HidingTheControlsAgain_OffThePicture_KeepsItHidden()
    {
        _cursor.ControlsHidden(isOverPicture: true);

        _cursor.ControlsHidden(isOverPicture: false);

        Assert.True(_cursor.IsHidden);
    }

    [Fact]
    public void LeavingThePlayer_ShowsTheCursor()
    {
        _cursor.ControlsHidden(isOverPicture: true);

        _cursor.Show();

        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void AfterLeaving_TheFirstMoveIsARealOne()
    {
        // The page loaded again: a small first move must not count as jitter against where the pointer was before.
        _cursor.Moves(100, 100);
        _cursor.Show();
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.True(_cursor.Moves(101, 100));
        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void AShownCursorOverTheControls_KeepsThemUp()
    {
        Assert.True(_cursor.HoldsControls(isOverControls: true));
    }

    [Fact]
    public void AHiddenCursorOverTheControls_KeepsNothingUp()
    {
        // Hidden by a remote's Back while it rested on the bar: the controls would never hide again.
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.False(_cursor.HoldsControls(isOverControls: true));
    }

    [Fact]
    public void ACursorOffTheControls_KeepsNothingUp()
    {
        Assert.False(_cursor.HoldsControls(isOverControls: false));
    }
}
