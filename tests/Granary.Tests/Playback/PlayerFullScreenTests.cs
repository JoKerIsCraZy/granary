using Granary.Core.Playback;
using Granary.Tests.Fakes;

namespace Granary.Tests.Playback;

/// <summary>Full screen for the next title: the player opens as it was last left on this PC.</summary>
public sealed class PlayerFullScreenTests
{
    private readonly InMemoryPlaybackDevicePreferences _preferences = new();
    private readonly ManualTimeProvider _time = new();

    private PlayerFullScreen Create() => new(_preferences, _time);

    [Fact]
    public void AtFirst_ATitleOpensThePlayerInAWindow() => Assert.False(Create().OpensInFullScreen);

    [Fact]
    public void LeftInFullScreen_TheNextTitleOpensInFullScreen()
    {
        var fullScreen = Create();

        fullScreen.Left(isFullScreen: true, byEsc: false);

        Assert.True(fullScreen.OpensInFullScreen);
        Assert.Equal([true], _preferences.SavedPlayerFullScreen);
    }

    [Fact]
    public void LeftInAWindow_TheNextTitleOpensInAWindow()
    {
        _preferences.PlayerFullScreen = true;
        var fullScreen = Create();

        fullScreen.Left(isFullScreen: false, byEsc: false);

        Assert.False(fullScreen.OpensInFullScreen);
        Assert.Equal([false], _preferences.SavedPlayerFullScreen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeftAsBefore_WritesNothing_AndHasNothingToWaitFor(bool isFullScreen)
    {
        _preferences.PlayerFullScreen = isFullScreen;
        var fullScreen = Create();

        var save = fullScreen.Left(isFullScreen, byEsc: false);

        Assert.Equal(isFullScreen, fullScreen.OpensInFullScreen);
        Assert.Empty(_preferences.SavedPlayerFullScreen);
        Assert.True(save.IsCompleted);
    }

    [Fact]
    public void AChangedChoice_HandsBackItsSave_SoClosingGranaryCanWaitForIt()
    {
        var written = new TaskCompletionSource();
        _preferences.PlayerFullScreenSave = written.Task;
        var fullScreen = Create();

        var save = fullScreen.Left(isFullScreen: true, byEsc: false);

        Assert.False(save.IsCompleted);
        written.SetResult();
        Assert.True(save.IsCompleted);
    }

    [Fact]
    public void KeptOnThisPC_ItHoldsForTheNextStartToo()
    {
        Create().Left(isFullScreen: true, byEsc: false);

        Assert.True(Create().OpensInFullScreen);
    }

    [Fact]
    public void EscOutOfFullScreen_ThenOutOfThePlayer_StillLeavesItInFullScreen()
    {
        // Esc in full screen leaves full screen first; the next Esc leaves the player. Both are the way out.
        var fullScreen = Create();

        fullScreen.EscapedFullScreen();
        fullScreen.Left(isFullScreen: false, byEsc: true);

        Assert.True(fullScreen.OpensInFullScreen);
    }

    [Fact]
    public void TheSecondEsc_ShortlyAfterTheFirst_StillLeavesItInFullScreen()
    {
        var fullScreen = Create();
        fullScreen.EscapedFullScreen();

        _time.Advance(PlayerFullScreen.EscapeWindow);
        fullScreen.Left(isFullScreen: false, byEsc: true);

        Assert.True(fullScreen.OpensInFullScreen);
    }

    [Fact]
    public void ALeaveThatIsNotAnEsc_ShortlyAfterAnEsc_IsLeftInAWindow()
    {
        // Esc out of full screen near the end, then the title ends, Back, another page or closing Granary: not the way out.
        _preferences.PlayerFullScreen = true;
        var fullScreen = Create();
        fullScreen.EscapedFullScreen();

        _time.Advance(TimeSpan.FromSeconds(1));
        fullScreen.Left(isFullScreen: false, byEsc: false);

        Assert.False(fullScreen.OpensInFullScreen);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    public void AnEscLongerAgo_WasToWatchInAWindow(int secondsPastTheWindow)
    {
        // Esc once to watch in the window, the title playing on, then Esc again: the window is the viewer's choice.
        _preferences.PlayerFullScreen = true;
        var fullScreen = Create();
        fullScreen.EscapedFullScreen();

        _time.Advance(PlayerFullScreen.EscapeWindow + TimeSpan.FromSeconds(secondsPastTheWindow));
        fullScreen.Left(isFullScreen: false, byEsc: true);

        Assert.False(fullScreen.OpensInFullScreen);
    }

    [Fact]
    public void AnotherChangeAfterEsc_IsTheViewersChoice()
    {
        // Back in full screen with F after an Esc, then out of it with F again: the window is the viewer's choice now.
        _preferences.PlayerFullScreen = true;
        var fullScreen = Create();
        fullScreen.EscapedFullScreen();

        fullScreen.ForgetEscape();
        fullScreen.Left(isFullScreen: false, byEsc: true);

        Assert.False(fullScreen.OpensInFullScreen);
    }

    [Fact]
    public void AnEsc_CountsForTheOneLeaveAfterIt()
    {
        var fullScreen = Create();
        fullScreen.EscapedFullScreen();
        fullScreen.Left(isFullScreen: false, byEsc: true);

        // The next title's player, left with Esc in a window.
        fullScreen.Left(isFullScreen: false, byEsc: true);

        Assert.False(fullScreen.OpensInFullScreen);
    }
}
