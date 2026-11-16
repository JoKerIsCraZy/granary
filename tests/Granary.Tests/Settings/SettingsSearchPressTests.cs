using Granary.App.Views;

namespace Granary.Tests.Settings;

/// <summary>
/// A press (a click, Enter, a remote's OK) on a sidebar entry while the settings' search narrows the pages puts focus on the
/// page's match: at once on the page that shows, else once the choice the press makes has opened it. A press waits only
/// when a choice will follow, so none is left over to pull focus into a page opened later by the arrows.
/// </summary>
public sealed class SettingsSearchPressTests
{
    [Fact]
    public void APress_OnThePageThatShows_FocusesItsMatchAtOnce()
    {
        var press = new SettingsSearchPress();

        Assert.True(press.Press("playback", openPage: "playback", showsOpenPage: true, byKeyboard: true));
        Assert.False(press.TryTake("playback", out _));
    }

    [Fact]
    public void APress_OnThePageTheEditorStandsInFor_LeavesNothingWaiting()
    {
        var press = new SettingsSearchPress();

        // The mpv.conf editor shows in Playback's place, and Playback stays chosen: nothing opens after the press.
        Assert.False(press.Press("playback", openPage: "playback", showsOpenPage: false, byKeyboard: false));

        Assert.False(press.TryTake("playback", out _));
    }

    [Fact]
    public void APress_OnAnotherEntry_WaitsForItsPageToOpen_Once()
    {
        var press = new SettingsSearchPress();

        Assert.False(press.Press("subtitles", openPage: "playback", showsOpenPage: true, byKeyboard: true));

        Assert.True(press.TryTake("subtitles", out bool byKeyboard));
        Assert.True(byKeyboard);
        Assert.False(press.TryTake("subtitles", out _));
    }

    [Fact]
    public void AWaitingPress_GoesWhenAnotherPageOpens()
    {
        var press = new SettingsSearchPress();
        press.Press("subtitles", openPage: "playback", showsOpenPage: true, byKeyboard: false);

        Assert.False(press.TryTake("about", out _));
        Assert.False(press.TryTake("subtitles", out _));
    }

    /// <summary>
    /// A click chooses its entry before its press comes; while the mpv.conf question holds the page back, the press waits with
    /// it: kept, the page opens and takes the focus; declined, the choice goes back and the press is forgotten.
    /// </summary>
    [Fact]
    public void APress_WhileItsPageWaitsOnTheEditorsQuestion_WaitsWithIt()
    {
        var press = new SettingsSearchPress();

        Assert.False(press.Press("subtitles", openPage: "playback", showsOpenPage: false, byKeyboard: false));

        Assert.True(press.TryTake("subtitles", out _));
    }

    [Fact]
    public void Forget_DropsAWaitingPress()
    {
        var press = new SettingsSearchPress();
        press.Press("subtitles", openPage: "playback", showsOpenPage: true, byKeyboard: false);

        press.Forget();

        Assert.False(press.TryTake("subtitles", out _));
    }
}
