using Granary.Core.Navigation;

namespace Granary.Tests.Navigation;

/// <summary>Back from a remote, as Silo's TV app: a section's content, then its sidebar entry, then Home, then nothing.</summary>
public sealed class BackLadderTests
{
    [Fact]
    public void APageOpenedWithinASection_GoesBackToThePageItCameFrom()
    {
        var step = BackLadder.Decide(new BackContext(FocusArea.Content, IsAtSection: false, IsHome: false, CanGoBack: true));

        Assert.Equal(BackStep.GoBack, step);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASectionsContent_MovesFocusToItsSidebarEntry_HomeToo(bool isHome)
    {
        var step = BackLadder.Decide(new BackContext(FocusArea.Content, IsAtSection: true, IsHome: isHome, CanGoBack: !isHome));

        Assert.Equal(BackStep.FocusSidebar, step);
    }

    [Theory]
    [InlineData(FocusArea.Sidebar)]
    [InlineData(FocusArea.None)]
    public void ASectionFromTheSidebar_GoesHome_AndTheSidebarKeepsFocus(FocusArea focus)
    {
        var step = BackLadder.Decide(new BackContext(focus, IsAtSection: true, IsHome: false, CanGoBack: true));

        Assert.Equal(BackStep.GoHomeInSidebar, step);
    }

    [Theory]
    [InlineData(FocusArea.Sidebar)]
    [InlineData(FocusArea.None)]
    public void Home_FromTheSidebar_DoesNothing_TheAppDoesNotQuit(FocusArea focus)
    {
        var step = BackLadder.Decide(new BackContext(focus, IsAtSection: true, IsHome: true, CanGoBack: false));

        Assert.Equal(BackStep.Nothing, step);
    }

    [Fact]
    public void APageWithNothingUnderIt_DoesNothing()
    {
        // A sign-in or a profile picker: reached by a reset, with no history.
        var step = BackLadder.Decide(new BackContext(FocusArea.Content, IsAtSection: false, IsHome: false, CanGoBack: false));

        Assert.Equal(BackStep.Nothing, step);
    }

    [Theory]
    [InlineData(BackKey.Escape, false, true)]
    [InlineData(BackKey.Escape, true, true)]
    [InlineData(BackKey.BrowserBack, false, true)]
    [InlineData(BackKey.BrowserBack, true, true)]
    [InlineData(BackKey.Backspace, false, true)]
    [InlineData(BackKey.Backspace, true, false)]
    public void Backspace_IsBack_OnlyWhenNothingIsTyped(BackKey key, bool isTyping, bool isBack)
    {
        Assert.Equal(isBack, BackLadder.IsBack(key, isTyping));
    }
}
