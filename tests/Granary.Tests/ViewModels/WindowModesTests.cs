using Granary.Core.ViewModels;

namespace Granary.Tests.ViewModels;

/// <summary>The window's one full-screen state, shared by the app, the player and a trailer.</summary>
public sealed class WindowModesTests
{
    [Fact]
    public void AtStart_ItIsAWindow()
    {
        var modes = new WindowModes();

        Assert.Equal(WindowMode.Window, modes.Mode);
        Assert.False(modes.IsFullScreen);
        Assert.False(modes.IsMiniView);
        Assert.False(modes.IsTrailerFullScreen);
    }

    [Fact]
    public void Toggling_GoesFullScreenAndBackToTheWindow()
    {
        var modes = new WindowModes();

        modes.ToggleFullScreen();
        Assert.Equal(WindowMode.FullScreen, modes.Mode);

        modes.ToggleFullScreen();
        Assert.Equal(WindowMode.Window, modes.Mode);
    }

    [Fact]
    public void LeavingTheMiniView_FromFullScreen_GoesBackToFullScreen()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);

        modes.SetMiniView(true);
        Assert.Equal(WindowMode.MiniView, modes.Mode);

        modes.SetMiniView(false);
        Assert.Equal(WindowMode.FullScreen, modes.Mode);
    }

    [Fact]
    public void LeavingTheMiniView_FromAWindow_GoesBackToTheWindow()
    {
        var modes = new WindowModes();

        modes.SetMiniView(true);
        modes.SetMiniView(false);

        Assert.Equal(WindowMode.Window, modes.Mode);
    }

    [Fact]
    public void FullScreenFromTheMiniView_GoesStraightThere_AndLeavingItGoesToTheWindow()
    {
        var modes = new WindowModes();
        modes.SetMiniView(true);

        modes.ToggleFullScreen();
        Assert.Equal(WindowMode.FullScreen, modes.Mode);

        modes.ToggleFullScreen();
        Assert.Equal(WindowMode.Window, modes.Mode);
    }

    [Fact]
    public void TheMiniViewOpenedAgain_RemembersOnlyWhereItCameFromThisTime()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);
        modes.SetMiniView(true);
        modes.SetMiniView(false);
        modes.SetFullScreen(false);

        modes.SetMiniView(true);
        modes.SetMiniView(false);

        Assert.Equal(WindowMode.Window, modes.Mode);
    }

    [Fact]
    public void LeavingFullScreenInTheMiniView_KeepsTheMiniView_AndGoesBackToTheWindowAfterIt()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);
        modes.SetMiniView(true);

        modes.SetFullScreen(false);
        Assert.Equal(WindowMode.MiniView, modes.Mode);

        modes.SetMiniView(false);
        Assert.Equal(WindowMode.Window, modes.Mode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingTheMiniViewOutsideIt_KeepsTheWindowAsItIs(bool fullScreen)
    {
        // The player's Leave and its bar end the mini view only: the window stays as the viewer set it.
        var modes = new WindowModes();
        modes.SetFullScreen(fullScreen);

        modes.SetMiniView(false);

        Assert.Equal(fullScreen, modes.IsFullScreen);
        Assert.False(modes.IsMiniView);
    }

    [Fact]
    public void ATrailersFullScreen_InsideFullScreen_ChangesNothingAboutTheWindow()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);

        modes.SetTrailerFullScreen(true);
        Assert.True(modes.IsTrailerFullScreen);
        Assert.Equal(WindowMode.FullScreen, modes.Mode);

        modes.SetTrailerFullScreen(false);
        Assert.False(modes.IsTrailerFullScreen);
        Assert.Equal(WindowMode.FullScreen, modes.Mode);
    }

    [Fact]
    public void ATrailersFullScreen_FromAWindow_GoesBackToTheWindowWithIt()
    {
        var modes = new WindowModes();

        modes.SetTrailerFullScreen(true);
        Assert.Equal(WindowMode.FullScreen, modes.Mode);

        modes.SetTrailerFullScreen(false);
        Assert.Equal(WindowMode.Window, modes.Mode);
    }

    [Fact]
    public void LeavingFullScreen_EndsTheTrailersToo()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);
        modes.SetTrailerFullScreen(true);

        modes.ToggleFullScreen();

        Assert.Equal(WindowMode.Window, modes.Mode);
        Assert.False(modes.IsTrailerFullScreen);
    }

    [Fact]
    public void FullScreenChosenDuringATrailersOwn_StaysAfterIt()
    {
        var modes = new WindowModes();
        modes.SetTrailerFullScreen(true);

        modes.SetFullScreen(true);
        modes.SetTrailerFullScreen(false);

        Assert.Equal(WindowMode.FullScreen, modes.Mode);
    }

    [Fact]
    public void ATrailersFullScreenThatLeftWithTheWindow_DoesNotTakeTheWindowBackLater()
    {
        var modes = new WindowModes();
        modes.SetTrailerFullScreen(true);
        modes.SetFullScreen(false);
        modes.SetFullScreen(true);

        modes.SetTrailerFullScreen(false);

        Assert.Equal(WindowMode.FullScreen, modes.Mode);
    }

    [Fact]
    public void AChangeTheWindowMadeItself_IsFollowed()
    {
        var modes = new WindowModes();
        modes.SetTrailerFullScreen(true);

        modes.Follow(WindowMode.Window);

        Assert.Equal(WindowMode.Window, modes.Mode);
        Assert.False(modes.IsTrailerFullScreen);
    }

    [Fact]
    public void FullScreenTheWindowTookItself_IsTheViewers_AndEndsWithTheirToggle()
    {
        var modes = new WindowModes();

        modes.Follow(WindowMode.FullScreen);
        Assert.True(modes.IsFullScreen);
        Assert.False(modes.IsTrailerFullScreen);

        // A trailer inside it does not take it over.
        modes.SetTrailerFullScreen(true);
        modes.SetTrailerFullScreen(false);
        Assert.True(modes.IsFullScreen);

        modes.ToggleFullScreen();
        Assert.Equal(WindowMode.Window, modes.Mode);
    }

    [Fact]
    public void AMiniViewTheWindowTookItself_GoesBackToTheWindow()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);

        modes.Follow(WindowMode.MiniView);
        Assert.True(modes.IsMiniView);

        modes.SetMiniView(false);
        Assert.Equal(WindowMode.Window, modes.Mode);
    }

    [Fact]
    public void AMiniViewLeftByTheWindowItself_ForgetsItCameFromFullScreen()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);
        modes.SetMiniView(true);

        modes.Follow(WindowMode.Window);
        modes.Follow(WindowMode.MiniView);
        modes.SetMiniView(false);

        Assert.Equal(WindowMode.Window, modes.Mode);
    }

    [Fact]
    public void ATrailersFullScreen_FromTheMiniView_GoesBackToTheWindowWithIt()
    {
        var modes = new WindowModes();
        modes.SetMiniView(true);

        modes.SetTrailerFullScreen(true);
        Assert.Equal(WindowMode.FullScreen, modes.Mode);
        Assert.True(modes.IsTrailerFullScreen);

        modes.SetTrailerFullScreen(false);
        Assert.Equal(WindowMode.Window, modes.Mode);
    }

    [Fact]
    public void ATrailersFullScreenAskedTwice_EndsWithOneLeave()
    {
        var modes = new WindowModes();
        modes.SetTrailerFullScreen(true);
        modes.SetTrailerFullScreen(true);

        modes.SetTrailerFullScreen(false);
        Assert.Equal(WindowMode.Window, modes.Mode);
        Assert.False(modes.IsTrailerFullScreen);

        modes.SetTrailerFullScreen(false);
        Assert.Equal(WindowMode.Window, modes.Mode);
    }

    [Fact]
    public void LeavingATrailersFullScreenAgain_InFullScreen_KeepsIt()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);
        modes.SetTrailerFullScreen(false);
        modes.SetTrailerFullScreen(true);
        modes.SetTrailerFullScreen(false);

        modes.SetTrailerFullScreen(false);

        Assert.Equal(WindowMode.FullScreen, modes.Mode);
    }

    [Fact]
    public void AWindow_IsNotTheViewersFullScreen() => Assert.False(new WindowModes().IsViewersFullScreen);

    [Fact]
    public void FullScreen_IsTheViewersFullScreen()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);

        Assert.True(modes.IsViewersFullScreen);
    }

    [Fact]
    public void TheMiniViewFromFullScreen_CountsAsTheFullScreenItGoesBackTo()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);
        modes.SetMiniView(true);

        Assert.True(modes.IsViewersFullScreen);
    }

    [Fact]
    public void TheMiniViewFromAWindow_IsNotTheViewersFullScreen()
    {
        var modes = new WindowModes();
        modes.SetMiniView(true);

        Assert.False(modes.IsViewersFullScreen);
    }

    [Fact]
    public void ATrailersOwnFullScreen_IsNotTheViewers_ButOneInsideTheViewersIs()
    {
        var modes = new WindowModes();
        modes.SetTrailerFullScreen(true);
        Assert.False(modes.IsViewersFullScreen);

        modes.SetTrailerFullScreen(false);
        modes.SetFullScreen(true);
        modes.SetTrailerFullScreen(true);
        Assert.True(modes.IsViewersFullScreen);
    }

    [Fact]
    public void ClosedAsAWindow_TheNextStartIsAWindow() => Assert.False(new WindowModes().ReopensInFullScreen(isMinimized: false));

    [Fact]
    public void ClosedInFullScreen_TheNextStartIsFullScreen()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);

        Assert.True(modes.ReopensInFullScreen(isMinimized: false));
    }

    [Fact]
    public void ClosedMinimized_TheNextStartIsAWindow_EvenFromFullScreen()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);

        Assert.False(modes.ReopensInFullScreen(isMinimized: true));
    }

    [Fact]
    public void ClosedInTheMiniView_TheNextStartIsAWindow_EvenFromFullScreen()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);
        modes.SetMiniView(true);

        Assert.False(modes.ReopensInFullScreen(isMinimized: false));
    }

    [Fact]
    public void ClosedInATrailersOwnFullScreen_TheNextStartIsTheWindowItWouldGoBackTo()
    {
        var modes = new WindowModes();
        modes.SetTrailerFullScreen(true);

        Assert.False(modes.ReopensInFullScreen(isMinimized: false));
    }

    [Fact]
    public void ClosedInFullScreenWithATrailerInIt_TheNextStartIsFullScreen()
    {
        var modes = new WindowModes();
        modes.SetFullScreen(true);
        modes.SetTrailerFullScreen(true);

        Assert.True(modes.ReopensInFullScreen(isMinimized: false));
    }

    [Fact]
    public void ClosedInAFullScreenTheWindowTookItself_TheNextStartIsFullScreen()
    {
        var modes = new WindowModes();
        modes.Follow(WindowMode.FullScreen);

        Assert.True(modes.ReopensInFullScreen(isMinimized: false));
    }

    [Fact]
    public void FullScreenAskedWhileATrailerHoldsIt_KeepsTheTrailersFullScreen()
    {
        var modes = new WindowModes();
        modes.SetTrailerFullScreen(true);

        modes.SetFullScreen(true);

        Assert.Equal(WindowMode.FullScreen, modes.Mode);
        Assert.True(modes.IsTrailerFullScreen);
    }
}
