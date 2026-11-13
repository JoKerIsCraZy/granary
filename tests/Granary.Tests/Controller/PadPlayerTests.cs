using Granary.Core.Controller;

namespace Granary.Tests.Controller;

/// <summary>A game controller in the full player: buttons, focus and the volume.</summary>
public sealed class PadPlayerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly PadBench _bench = new(new PadContext(PadMode.Player, CanSeek: true));

    private void Focus(PadFocus focus, bool hasPrompt = false) =>
        _bench.Context = _bench.Context with { Focus = focus, HasPrompt = hasPrompt };

    [Fact]
    public void AOnThePicture_PlaysOrPauses_AtOnce()
    {
        var pressed = _bench.Read(PadButtons.A);
        var held = _bench.Hold(PadButtons.A, Second);
        var up = _bench.Read(default);

        Assert.Equal([new PadAction(PadActionKind.PlayPause)], pressed);
        Assert.Empty(held);
        Assert.Empty(up);
    }

    [Theory]
    [InlineData(PadFocus.Controls)]
    [InlineData(PadFocus.Menu)]
    public void AOnAControlOrInAMenu_PressesWhatHasFocus(PadFocus focus)
    {
        Focus(focus);

        Assert.Equal([new PadAction(PadActionKind.Activate)], _bench.Read(PadButtons.A));
    }

    [Fact]
    public void AWhileSkipIntroShows_SkipsRatherThanPauses()
    {
        Focus(PadFocus.Picture, hasPrompt: true);

        Assert.Equal([new PadAction(PadActionKind.TriggerPrompt)], _bench.Read(PadButtons.A));
    }

    [Fact]
    public void AOnAControlWhileSkipIntroShows_StillPressesTheControl()
    {
        Focus(PadFocus.Controls, hasPrompt: true);

        Assert.Equal([new PadAction(PadActionKind.Activate)], _bench.Read(PadButtons.A));
    }

    [Theory]
    [InlineData(PadFocus.Picture)]
    [InlineData(PadFocus.Controls)]
    [InlineData(PadFocus.Menu)]
    public void X_PlaysOrPauses_WhereverFocusIs(PadFocus focus)
    {
        Focus(focus, hasPrompt: true);

        Assert.Equal([new PadAction(PadActionKind.TogglePause)], _bench.Hold(PadButtons.X, Second));
    }

    [Theory]
    [InlineData(PadButtons.B, PadActionKind.Back)]
    [InlineData(PadButtons.Y, PadActionKind.AudioAndSubtitles)]
    [InlineData(PadButtons.Menu, PadActionKind.PlayerSettings)]
    [InlineData(PadButtons.View, PadActionKind.PlaybackInfo)]
    [InlineData(PadButtons.LeftShoulder, PadActionKind.PreviousChapter)]
    [InlineData(PadButtons.RightShoulder, PadActionKind.NextChapter)]
    [InlineData(PadButtons.LeftStick, PadActionKind.ToggleSubtitles)]
    [InlineData(PadButtons.RightStick, PadActionKind.ToggleFullScreen)]
    public void AButton_ActsOnce_HoweverLongItIsHeld(PadButtons button, PadActionKind expected)
    {
        Assert.Equal([new PadAction(expected)], _bench.Hold(button, 2 * Second));
    }

    [Theory]
    [InlineData(PadButtons.DPadUp)]
    [InlineData(PadButtons.DPadDown)]
    public void UpOrDownOnThePicture_BringsTheBar_Once(PadButtons button)
    {
        var pressed = _bench.Read(button);
        // The bar now has focus; the press that brought it moves nothing in it.
        Focus(PadFocus.Controls);
        var held = _bench.Hold(button, Second);

        Assert.Equal([new PadAction(PadActionKind.EnterBar)], pressed);
        Assert.Empty(held);
    }

    [Theory]
    [InlineData(PadFocus.Controls)]
    [InlineData(PadFocus.Menu)]
    public void TheDPadInTheControlsOrAMenu_MovesFocus_AndSeeksNothing(PadFocus focus)
    {
        Focus(focus);

        var actions = _bench.Hold(PadButtons.DPadLeft, Second);
        actions.AddRange(_bench.Rest(Second));

        Assert.Equal(PadAction.Move(PadDirection.Left), actions[0]);
        Assert.All(actions, action => Assert.Equal(PadActionKind.MoveFocus, action.Kind));
    }

    [Fact]
    public void TheRightStick_SetsTheVolume_AndAgainWhileHeld()
    {
        var first = _bench.Read(new PadReading(PadButtons.None, RightY: 0.9));
        var held = _bench.Hold(new PadReading(PadButtons.None, RightY: 0.9), Second);
        _bench.Read(default);
        var down = _bench.Read(new PadReading(PadButtons.None, RightY: -1));

        Assert.Equal([PadAction.Of(PadActionKind.Volume, PadTuning.VolumeStep)], first);
        Assert.True(held.Count >= 3);
        Assert.All(held, action => Assert.Equal(PadAction.Of(PadActionKind.Volume, PadTuning.VolumeStep), action));
        Assert.Equal([PadAction.Of(PadActionKind.Volume, -PadTuning.VolumeStep)], down);
    }

    [Fact]
    public void TheRightStickSideways_DoesNothingInThePlayer()
    {
        Assert.Empty(_bench.Hold(new PadReading(PadButtons.None, RightX: 1), Second));
    }

    [Fact]
    public void InAMenu_TheBumpersTriggersAndTheRightStickWait()
    {
        Focus(PadFocus.Menu);

        var actions = _bench.Hold(new PadReading(PadButtons.LeftShoulder | PadButtons.RightShoulder, RightY: 1, LeftTrigger: 1, RightTrigger: 1), Second);
        actions.AddRange(_bench.Rest(Second));

        Assert.Empty(actions);
    }

    [Fact]
    public void AButtonHeldAsThePlayerOpens_DoesNothingThere()
    {
        var bench = new PadBench();
        bench.Read(PadButtons.X);
        bench.Context = new PadContext(PadMode.Player, CanSeek: true);

        var held = bench.Hold(PadButtons.X, Second);

        Assert.Empty(held);
    }
}
