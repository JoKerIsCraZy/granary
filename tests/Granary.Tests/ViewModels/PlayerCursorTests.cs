using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>
/// The player's cursor follows the mouse only: a real move shows it, it hides with the controls over the picture whatever
/// hid them, and while they are hidden it hides as soon as it is seen over the picture again.
/// </summary>
public sealed class PlayerCursorTests
{
    private const double Scale = 1;

    private readonly ManualTimeProvider _time = new();
    private readonly PlayerCursor _cursor;

    public PlayerCursorTests() => _cursor = new PlayerCursor(_time);

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
        _cursor.Moves(100, 100, Scale);
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.True(_cursor.Moves(110, 100, Scale));
        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void TheFirstMove_WhereThePointerWasNotKnown_IsARealOne()
    {
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.True(_cursor.Moves(5, 5, Scale));
        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void APointerResting_AsThePlayerOpens_DoesNotMove()
    {
        // The framework tells the page where the pointer is as it loads under it: that is no move.
        _cursor.RestsAt(100, 100);
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.False(_cursor.Moves(100, 100, Scale));
        Assert.True(_cursor.IsHidden);
    }

    [Fact]
    public void AMove_AfterThePlayerOpens_IsARealOne()
    {
        _cursor.RestsAt(100, 100);
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.True(_cursor.Moves(110, 100, Scale));
        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void AStillPointerTestedAgain_LeavesTheCursorHidden()
    {
        // A key brings the controls back under the resting pointer, and the framework tests it again where it is.
        _cursor.Moves(100, 100, Scale);
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.False(_cursor.Moves(100, 100, Scale));
        Assert.True(_cursor.IsHidden);
    }

    [Fact]
    public void Jitter_LeavesTheCursorHidden()
    {
        _cursor.Moves(100, 100, Scale);
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.False(_cursor.Moves(101, 101.5, Scale));
        Assert.True(_cursor.IsHidden);
    }

    [Fact]
    public void SmallMoves_CountOnceTheyAddUpQuickly()
    {
        _cursor.Moves(100, 100, Scale);
        _cursor.ControlsHidden(isOverPicture: true);
        _time.Advance(TimeSpan.FromMilliseconds(16));
        _cursor.Moves(101, 100, Scale);
        _time.Advance(TimeSpan.FromMilliseconds(16));

        Assert.True(_cursor.Moves(100 + PlayerCursor.MoveThreshold, 100, Scale));
        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void SlowDrift_NeverAddsUp()
    {
        // A mouse that creeps a little now and then, or a knock on the desk: in full screen it brought the cursor back.
        _cursor.Moves(100, 100, Scale);
        _cursor.ControlsHidden(isOverPicture: true);

        for (int step = 1; step <= 10; step++)
        {
            _time.Advance(PlayerCursor.MoveWindow + TimeSpan.FromMilliseconds(100));
            Assert.False(_cursor.Moves(100 + (step * 0.5), 100, Scale));
        }
        Assert.True(_cursor.IsHidden);
    }

    [Fact]
    public void AQuickMove_AfterALongRest_IsARealOne()
    {
        _cursor.Moves(100, 100, Scale);
        _cursor.ControlsHidden(isOverPicture: true);
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.True(_cursor.Moves(140, 120, Scale));
        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void AtAHigherScale_AMoveNeedsMorePixels()
    {
        // Positions are the screen's pixels, the threshold the page's: at 150 % a move needs 3 of them.
        _cursor.Moves(100, 100, 1.5);
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.False(_cursor.Moves(102, 100, 1.5));
        Assert.True(_cursor.Moves(103, 100, 1.5));
    }

    [Fact]
    public void ATouchOrPenMove_IsAlwaysARealOne()
    {
        // Only the mouse's place is measured on the screen: a finger or a pen dragged over the picture shows the controls.
        _cursor.Moves(100, 100, Scale);
        _cursor.ControlsHidden(isOverPicture: true);

        _cursor.TouchOrPenMoves();

        Assert.False(_cursor.IsHidden);
        Assert.False(_cursor.LooksAgain(isOverPicture: true));
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
        // The page loaded again where the pointer was not known: a small first move must not count as jitter against
        // where the pointer was before.
        _cursor.Moves(100, 100, Scale);
        _cursor.Show();
        _cursor.ControlsHidden(isOverPicture: true);

        Assert.True(_cursor.Moves(101, 100, Scale));
        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void ACursorLeftShownByAnotherWindow_HidesOnceSeenOverThePicture()
    {
        // A notification or the volume flyout was under the pointer as the controls hid, and went.
        _cursor.ControlsHidden(isOverPicture: false);

        Assert.True(_cursor.WaitsForPicture);
        Assert.True(_cursor.LooksAgain(isOverPicture: true));
        Assert.True(_cursor.IsHidden);
        Assert.False(_cursor.WaitsForPicture);
    }

    [Fact]
    public void ALookAgain_OffThePicture_LeavesTheCursor()
    {
        _cursor.ControlsHidden(isOverPicture: false);

        Assert.False(_cursor.LooksAgain(isOverPicture: false));
        Assert.False(_cursor.IsHidden);
        Assert.True(_cursor.WaitsForPicture);
    }

    [Fact]
    public void ALookAgain_WhileTheControlsShow_LeavesTheCursor()
    {
        _cursor.ControlsHidden(isOverPicture: false);
        _cursor.ControlsShown();

        Assert.False(_cursor.LooksAgain(isOverPicture: true));
        Assert.False(_cursor.IsHidden);
        Assert.False(_cursor.WaitsForPicture);
    }

    [Fact]
    public void ALookAgain_AfterARealMove_LeavesTheCursor()
    {
        // The move brings the controls back with the cursor.
        _cursor.Moves(100, 100, Scale);
        _cursor.ControlsHidden(isOverPicture: true);
        _cursor.Moves(150, 100, Scale);

        Assert.False(_cursor.LooksAgain(isOverPicture: true));
        Assert.False(_cursor.IsHidden);
    }

    [Fact]
    public void ALook_IsDueOnlyWhileTheCursorWaitsForThePicture()
    {
        // Pointer events that are no move come at the mouse's report rate: a hidden cursor needs no look at all.
        Assert.False(_cursor.IsLookDue);

        _cursor.ControlsHidden(isOverPicture: true);
        Assert.False(_cursor.IsLookDue);

        _cursor.Show();
        _cursor.ControlsHidden(isOverPicture: false);
        Assert.True(_cursor.IsLookDue);
    }

    [Fact]
    public void AfterALook_TheNextIsDueOnlyAWhileLater()
    {
        _cursor.ControlsHidden(isOverPicture: false);
        _cursor.LooksAgain(isOverPicture: false);

        Assert.False(_cursor.IsLookDue);
        _time.Advance(PlayerCursor.LookInterval - TimeSpan.FromMilliseconds(1));
        Assert.False(_cursor.IsLookDue);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_cursor.IsLookDue);
    }

    [Fact]
    public void ALookAgain_AfterLeaving_LeavesTheCursor()
    {
        _cursor.ControlsHidden(isOverPicture: false);
        _cursor.Show();

        Assert.False(_cursor.LooksAgain(isOverPicture: true));
        Assert.False(_cursor.IsHidden);
    }
}
