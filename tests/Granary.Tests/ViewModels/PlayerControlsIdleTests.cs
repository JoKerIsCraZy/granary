using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>
/// What keeps the player's controls up once the idle wait has run out: as on Silo's web player, not where the pointer
/// rests, only what the viewer has open or is doing.
/// </summary>
public sealed class PlayerControlsIdleTests
{
    private static readonly PlayerControlsIdle Playing = new() { IsPlaying = true };

    [Fact]
    public void Playing_WithNothingOpen_KeepsNothing()
    {
        // Wherever the pointer rests: on the bar, at the edge of the screen or over Exit full screen.
        Assert.Equal(ControlsHold.None, Playing.Hold);
    }

    [Fact]
    public void Paused_KeepsThem()
    {
        Assert.Equal(ControlsHold.Paused, (Playing with { IsPaused = true }).Hold);
    }

    [Fact]
    public void NotPlaying_KeepsThem()
    {
        // Preparing, failed or ended.
        Assert.Equal(ControlsHold.NotPlaying, new PlayerControlsIdle().Hold);
    }

    [Fact]
    public void AnOpenMenu_KeepsThem()
    {
        Assert.Equal(ControlsHold.Menu, (Playing with { HasOpenMenu = true }).Hold);
    }

    [Fact]
    public void ASeekDrag_KeepsThem()
    {
        Assert.Equal(ControlsHold.SeekDrag, (Playing with { IsDraggingSeekBar = true }).Hold);
    }

    [Fact]
    public void AControlReachedWithTheKeyboard_KeepsThem()
    {
        Assert.Equal(ControlsHold.KeyboardFocus, (Playing with { HasKeyboardFocus = true }).Hold);
    }

    [Fact]
    public void ARemotesFocus_KeepsNothing()
    {
        // Silo's TV app hides its controls after a while without a key, focus or not.
        Assert.Equal(ControlsHold.None, (Playing with { HasKeyboardFocus = true, MovesThroughControls = true }).Hold);
    }

    [Fact]
    public void AnOpenPanel_KeepsThem()
    {
        Assert.Equal(ControlsHold.Panel, (Playing with { IsPanelOpen = true }).Hold);
    }

    [Fact]
    public void EditingTheMarkers_KeepsThem()
    {
        Assert.Equal(ControlsHold.MarkerEditor, (Playing with { IsEditingMarkers = true }).Hold);
    }

    [Fact]
    public void Buffering_KeepsThem()
    {
        Assert.Equal(ControlsHold.Buffering, (Playing with { IsBuffering = true }).Hold);
    }

    [Fact]
    public void TheMinimizedBar_KeepsThem()
    {
        Assert.Equal(ControlsHold.Docked, (Playing with { IsDocked = true }).Hold);
    }
}
