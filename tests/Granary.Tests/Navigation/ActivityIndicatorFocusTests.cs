using Granary.Core.Navigation;

namespace Granary.Tests.Navigation;

/// <summary>
/// The keyboard's and remote's way to the administrator's activity indicator, which lies outside the page at its top
/// right: Up at the page's top edge goes to it, but never past the way back, which lies there too.
/// </summary>
public sealed class ActivityIndicatorFocusTests
{
    [Theory]
    // Nothing above in the page: on to the indicator.
    [InlineData(ActivityFocusPlace.Page, UpTarget.Nothing, ActivityStep.FocusIndicator)]
    // The arrows would leave the page for the sidebar or the indicator itself: to the indicator.
    [InlineData(ActivityFocusPlace.Page, UpTarget.Elsewhere, ActivityStep.FocusIndicator)]
    // More of the page above: the arrows move within it.
    [InlineData(ActivityFocusPlace.Page, UpTarget.Page, ActivityStep.LeaveToArrows)]
    // The way back above: the arrows go to it, as before there was an indicator.
    [InlineData(ActivityFocusPlace.Page, UpTarget.PageBack, ActivityStep.LeaveToArrows)]
    // From the way back, which has nothing above it, on to the indicator at the other end of the page's top.
    [InlineData(ActivityFocusPlace.PageBack, UpTarget.Nothing, ActivityStep.FocusIndicator)]
    [InlineData(ActivityFocusPlace.PageBack, UpTarget.Elsewhere, ActivityStep.FocusIndicator)]
    // The sidebar and the indicator itself keep their own arrows.
    [InlineData(ActivityFocusPlace.Elsewhere, UpTarget.Nothing, ActivityStep.LeaveToArrows)]
    [InlineData(ActivityFocusPlace.Elsewhere, UpTarget.PageBack, ActivityStep.LeaveToArrows)]
    public void UpAtThePagesTop_GoesToTheIndicator_ButNotPastTheWayBack(ActivityFocusPlace from, UpTarget above, ActivityStep expected) =>
        Assert.Equal(expected, ActivityIndicatorFocus.Up(showsIndicator: true, from, above, isRepeat: false));

    [Theory]
    [InlineData(ActivityFocusPlace.Page, UpTarget.Nothing)]
    [InlineData(ActivityFocusPlace.Page, UpTarget.Elsewhere)]
    [InlineData(ActivityFocusPlace.PageBack, UpTarget.Nothing)]
    public void AHeldKey_StopsAtTheEdge(ActivityFocusPlace from, UpTarget above) =>
        Assert.Equal(ActivityStep.Stay, ActivityIndicatorFocus.Up(showsIndicator: true, from, above, isRepeat: true));

    [Theory]
    [InlineData(ActivityFocusPlace.Page, UpTarget.Page)]
    [InlineData(ActivityFocusPlace.Page, UpTarget.PageBack)]
    public void AHeldKey_GoesOnWhereTheArrowsDo(ActivityFocusPlace from, UpTarget above) =>
        Assert.Equal(ActivityStep.LeaveToArrows, ActivityIndicatorFocus.Up(showsIndicator: true, from, above, isRepeat: true));

    [Theory]
    [InlineData(ActivityFocusPlace.Page, UpTarget.Nothing)]
    [InlineData(ActivityFocusPlace.Page, UpTarget.Elsewhere)]
    [InlineData(ActivityFocusPlace.PageBack, UpTarget.Nothing)]
    public void WithoutAnIndicator_TheArrowsAreLeftAlone(ActivityFocusPlace from, UpTarget above)
    {
        Assert.Equal(ActivityStep.LeaveToArrows, ActivityIndicatorFocus.Up(showsIndicator: false, from, above, isRepeat: false));
        Assert.Equal(ActivityStep.LeaveToArrows, ActivityIndicatorFocus.Up(showsIndicator: false, from, above, isRepeat: true));
    }

    [Fact]
    public void AnIndicatorThatGoesWhileItHasFocus_GivesFocusBackToThePage()
    {
        // The last activity ended, or its panel closed on a page where it shows only while open.
        Assert.True(ActivityIndicatorFocus.GivesFocusBack(holdsFocus: true, isAppShown: true));
        // Focus is elsewhere: nothing to give back.
        Assert.False(ActivityIndicatorFocus.GivesFocusBack(holdsFocus: false, isAppShown: true));
        // The full player took the window: focus is the player's.
        Assert.False(ActivityIndicatorFocus.GivesFocusBack(holdsFocus: true, isAppShown: false));
    }

    [Theory]
    // A column in the middle of a wide page ends well before the indicator.
    [InlineData(1400, 1068, 0)]
    // The column ends right where the indicator's room begins.
    [InlineData(1132, 1068, 0)]
    // A narrow page: the column reaches the window's edge, less its own 16 px.
    [InlineData(700, 684, 48)]
    [InlineData(800, 768, 32)]
    // Never a negative inset, and none for what is no place.
    [InlineData(700, 0, 0)]
    [InlineData(double.NaN, 684, 0)]
    [InlineData(700, double.NaN, 0)]
    public void WhatEndsUnderTheIndicator_IsMovedClearOfIt(double pageWidth, double right, double expected) =>
        Assert.Equal(expected, ActivityIndicatorPlace.InsetFor(pageWidth, right));

    [Fact]
    public void TheIndicatorsRoom_IsItsDiscItsMarginAndAGap() =>
        Assert.Equal(ActivityIndicatorPlace.EdgeMargin + ActivityIndicatorPlace.Size + ActivityIndicatorPlace.Gap, ActivityIndicatorPlace.Room);
}
