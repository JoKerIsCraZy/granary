using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>The right of the player's bar: its buttons in the profile's order, and which it hides.</summary>
public sealed class PlayerBarTests
{
    private static readonly PlayerButton[] Today =
    [
        PlayerButton.Volume, PlayerButton.SleepTimer, PlayerButton.Audio, PlayerButton.Subtitles, PlayerButton.Chapters,
        PlayerButton.Settings, PlayerButton.EditMarkers, PlayerButton.PlaybackInfo, PlayerButton.MiniView, PlayerButton.Fill,
        PlayerButton.FullScreen,
    ];

    [Fact]
    public void TheDefault_IsTodaysBar_EveryButtonShown()
    {
        var bar = PlayerBar.Default;

        Assert.Equal(Today, bar.Order);
        Assert.All(Today, button => Assert.True(bar.Shows(button)));
        Assert.True(bar.IsDefault);
        Assert.Equal(Today, PlayerBar.Buttons.Select(info => info.Button));
    }

    [Fact]
    public void EachButton_HasItsOwnId()
    {
        Assert.Equal(PlayerBar.Buttons.Count, PlayerBar.Buttons.Select(info => info.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("sleep_timer", PlayerBar.Buttons.Single(info => info.Button == PlayerButton.SleepTimer).Id);
    }

    [Fact]
    public void AButtonHidden_IsNoLongerShown_AndShownAgain_TheBarIsTodays()
    {
        var hidden = PlayerBar.Default.WithShown(PlayerButton.PlaybackInfo, false);

        Assert.False(hidden.Shows(PlayerButton.PlaybackInfo));
        Assert.False(hidden.PlaybackInfo);
        Assert.False(hidden.IsDefault);
        Assert.Equal(PlayerBar.Default, hidden.WithShown(PlayerButton.PlaybackInfo, true));
    }

    [Fact]
    public void AButtonMoved_TakesTheNextPlace_AndMovedBack_TheBarIsTodays()
    {
        var moved = PlayerBar.Default.WithMoved(PlayerButton.Audio, -1);

        Assert.Equal([PlayerButton.Volume, PlayerButton.Audio, PlayerButton.SleepTimer], moved.Order.Take(3));
        Assert.False(moved.IsDefault);
        Assert.Equal(PlayerBar.Default, moved.WithMoved(PlayerButton.Audio, 1));
    }

    [Fact]
    public void TheFirstMovedUp_AndTheLastMovedDown_StayWhereTheyAre()
    {
        Assert.Equal(PlayerBar.Default, PlayerBar.Default.WithMoved(PlayerButton.Volume, -1));
        Assert.Equal(PlayerBar.Default, PlayerBar.Default.WithMoved(PlayerButton.FullScreen, 1));
    }

    [Fact]
    public void AButtonTheProfileHasNotPlaced_TakesItsOwnPlace_AfterTheOneBeforeItByDefault()
    {
        // Saved before Chapters and Fill screen came: they show where they belong, around what the profile moved.
        var bar = new PlayerBar(
            [PlayerButton.FullScreen, PlayerButton.Volume, PlayerButton.SleepTimer, PlayerButton.Audio, PlayerButton.Subtitles,
             PlayerButton.Settings, PlayerButton.EditMarkers, PlayerButton.PlaybackInfo, PlayerButton.MiniView],
            []);

        Assert.Equal(
            [PlayerButton.FullScreen, PlayerButton.Volume, PlayerButton.SleepTimer, PlayerButton.Audio, PlayerButton.Subtitles,
             PlayerButton.Chapters, PlayerButton.Settings, PlayerButton.EditMarkers, PlayerButton.PlaybackInfo, PlayerButton.MiniView,
             PlayerButton.Fill],
            bar.Order);
        Assert.True(bar.Shows(PlayerButton.Chapters));
    }

    [Fact]
    public void ANewFirstButton_ComesFirst()
    {
        var bar = new PlayerBar([PlayerButton.Audio, PlayerButton.SleepTimer], []);

        Assert.Equal([PlayerButton.Volume, PlayerButton.Audio, PlayerButton.Subtitles], bar.Order.Take(3));
        Assert.Equal(Today.Length, bar.Order.Count);
    }

    [Fact]
    public void AButtonNamedTwice_ShowsOnce()
    {
        var bar = new PlayerBar([PlayerButton.Audio, PlayerButton.Audio, PlayerButton.Volume], []);

        Assert.Equal(Today.Length, bar.Order.Count);
        Assert.Equal(PlayerButton.Audio, bar.Order[0]);
    }

    [Fact]
    public void ABarOfTheSameButtons_InTheSameOrder_IsTheSameBar()
    {
        var one = new PlayerBar(Today.Reverse(), [PlayerButton.Fill, PlayerButton.Volume]);
        var other = new PlayerBar(Today.Reverse(), [PlayerButton.Volume, PlayerButton.Fill]);

        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
        Assert.NotEqual(one, other.WithShown(PlayerButton.Fill, true));
    }

    [Fact]
    public void EverythingHidden_IsAllowed()
    {
        var bar = Today.Aggregate(PlayerBar.Default, (current, button) => current.WithShown(button, false));

        Assert.All(Today, button => Assert.False(bar.Shows(button)));
        Assert.Equal(Today, bar.Order);
    }
}
