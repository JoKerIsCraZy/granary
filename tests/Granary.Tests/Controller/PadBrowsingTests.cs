using Granary.Core.Controller;

namespace Granary.Tests.Controller;

/// <summary>A game controller on the pages: what its buttons, sticks and triggers ask for.</summary>
public sealed class PadBrowsingTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly PadBench _bench = new();

    [Fact]
    public void ATapOnA_PressesTheFocusedElement_WhenItIsLetGo()
    {
        var down = _bench.Hold(PadButtons.A, TimeSpan.FromMilliseconds(100));
        var up = _bench.Read(default);

        Assert.Empty(down);
        Assert.Equal([new PadAction(PadActionKind.Activate)], up);
    }

    [Fact]
    public void AHeldForHalfASecond_OpensTheItemsMenu_AndLettingGoPressesNothing()
    {
        var held = _bench.Hold(PadButtons.A, Second);
        var up = _bench.Read(default);

        Assert.Equal([new PadAction(PadActionKind.ContextMenu)], held);
        Assert.Empty(up);
    }

    [Theory]
    [InlineData(PadButtons.B, PadActionKind.Back)]
    [InlineData(PadButtons.X, PadActionKind.PlayFocused)]
    [InlineData(PadButtons.Y, PadActionKind.Search)]
    [InlineData(PadButtons.View, PadActionKind.ToggleSidebar)]
    [InlineData(PadButtons.Menu, PadActionKind.ContextMenu)]
    [InlineData(PadButtons.LeftShoulder, PadActionKind.PreviousTab)]
    [InlineData(PadButtons.RightShoulder, PadActionKind.NextTab)]
    [InlineData(PadButtons.RightStick, PadActionKind.ToggleFullScreen)]
    public void AButton_ActsOnce_HoweverLongItIsHeld(PadButtons button, PadActionKind expected)
    {
        var actions = _bench.Hold(button, 2 * Second);

        Assert.Equal([new PadAction(expected)], actions);
    }

    [Fact]
    public void TheLeftSticksClick_DoesNothingOnThePages()
    {
        Assert.Empty(_bench.Tap(PadButtons.LeftStick));
    }

    [Fact]
    public void TheDPad_MovesFocusAtOnce_ThenAgainWhileHeld()
    {
        var first = _bench.Read(PadButtons.DPadRight);
        var waiting = _bench.Hold(PadButtons.DPadRight, TimeSpan.FromMilliseconds(350));
        var repeating = _bench.Hold(PadButtons.DPadRight, Second);

        Assert.Equal([PadAction.Move(PadDirection.Right)], first);
        Assert.Empty(waiting);
        Assert.All(repeating, action => Assert.Equal(PadAction.Move(PadDirection.Right, isRepeat: true), action));
        // About nine a second, as a held key.
        Assert.InRange(repeating.Count, 7, 10);
    }

    [Theory]
    [InlineData(PadButtons.DPadUp, PadDirection.Up)]
    [InlineData(PadButtons.DPadDown, PadDirection.Down)]
    [InlineData(PadButtons.DPadLeft, PadDirection.Left)]
    [InlineData(PadButtons.DPadRight, PadDirection.Right)]
    public void EachDPadButton_MovesItsWay(PadButtons button, PadDirection expected)
    {
        Assert.Equal([PadAction.Move(expected)], _bench.Tap(button));
    }

    [Fact]
    public void ADiagonalOnTheDPad_KeepsTheWayItWasGoing()
    {
        _bench.Read(PadButtons.DPadRight);

        var diagonal = _bench.Hold(PadButtons.DPadRight | PadButtons.DPadUp, TimeSpan.FromMilliseconds(200));

        Assert.Empty(diagonal);
    }

    [Fact]
    public void TheLeftStick_MovesFocus_FromSevenTenths_AndLetsGoAtAHalf()
    {
        var below = _bench.Read(new PadReading(PadButtons.None, LeftX: 0.65));
        var pressed = _bench.Read(new PadReading(PadButtons.None, LeftX: 0.72));
        // Wobbling between the two thresholds is still the same push.
        var wobble = _bench.Hold(new PadReading(PadButtons.None, LeftX: 0.55), TimeSpan.FromMilliseconds(200));
        _bench.Read(new PadReading(PadButtons.None, LeftX: 0.45));
        var again = _bench.Read(new PadReading(PadButtons.None, LeftX: 0.8));

        Assert.Empty(below);
        Assert.Equal([PadAction.Move(PadDirection.Right)], pressed);
        Assert.Empty(wobble);
        Assert.Equal([PadAction.Move(PadDirection.Right)], again);
    }

    [Fact]
    public void TheLeftStick_GoesItsStrongerWayOnly()
    {
        var actions = _bench.Read(new PadReading(PadButtons.None, LeftX: -0.6, LeftY: 0.75));

        Assert.Equal([PadAction.Move(PadDirection.Up)], actions);
    }

    [Fact]
    public void AStickThatDrifts_DoesNothing_AndIsNoActivity()
    {
        var actions = _bench.Hold(new PadReading(PadButtons.None, LeftX: 0.15, LeftY: -0.1, RightX: 0.2, RightY: 0.1, LeftTrigger: 0.08), Second);

        Assert.Empty(actions);
        Assert.False(_bench.Pad.IsActive);
        Assert.True(_bench.Pad.IsIdle);
    }

    [Fact]
    public void AStickPushedALittle_IsActivity_ButMovesNothing()
    {
        var actions = _bench.Hold(new PadReading(PadButtons.None, LeftX: 0.4), Second);

        Assert.Empty(actions);
        Assert.True(_bench.Pad.IsActive);
    }

    [Fact]
    public void TheTriggers_PageUpAndDown_FromHalfWay()
    {
        var light = _bench.Read(new PadReading(PadButtons.None, RightTrigger: 0.4));
        var down = _bench.Read(new PadReading(PadButtons.None, RightTrigger: 0.6));
        _bench.Read(default);
        var up = _bench.Read(new PadReading(PadButtons.None, LeftTrigger: 1));

        Assert.Empty(light);
        Assert.Equal([new PadAction(PadActionKind.PageDown)], down);
        Assert.Equal([new PadAction(PadActionKind.PageUp)], up);
    }

    [Fact]
    public void AHeldTrigger_PagesAgain()
    {
        var actions = _bench.Hold(new PadReading(PadButtons.None, RightTrigger: 1), Second);

        Assert.True(actions.Count >= 3);
        Assert.All(actions, action => Assert.Equal(PadActionKind.PageDown, action.Kind));
    }

    [Fact]
    public void ATriggerAPadReportsAsAButton_PagesToo()
    {
        Assert.Equal([new PadAction(PadActionKind.PageDown)], _bench.Read(PadButtons.RightTrigger));
    }

    [Fact]
    public void TheRightStick_ScrollsThePage_AndMovesNoFocus()
    {
        var up = _bench.Hold(new PadReading(PadButtons.None, RightY: 1), Second);
        _bench.Read(default);
        var gentle = _bench.Hold(new PadReading(PadButtons.None, RightY: -0.5), Second);

        Assert.All(up, action => Assert.Equal(PadActionKind.Scroll, action.Kind));
        double full = up.Sum(action => action.Y);
        double half = gentle.Sum(action => action.Y);
        Assert.Equal(PadTuning.ScrollSpeed, full, PadTuning.ScrollSpeed * 0.05);
        Assert.True(half < 0);
        Assert.True(Math.Abs(half) < full / 2);
    }

    [Fact]
    public void AButtonHeldWhenThePadIsFirstRead_DoesNothingUntilItIsPressedAgain()
    {
        var bench = new PadBench();
        bench.Pad.Reset();

        var held = bench.Hold(PadButtons.B | PadButtons.DPadDown, Second);
        bench.Read(default);
        var pressed = bench.Read(PadButtons.B);

        Assert.Empty(held);
        Assert.Equal([new PadAction(PadActionKind.Back)], pressed);
    }

    [Fact]
    public void AHeldIntoThePlayer_PausesNothing()
    {
        _bench.Read(PadButtons.A);
        _bench.Context = new PadContext(PadMode.Player);

        var held = _bench.Hold(PadButtons.A, Second);
        var up = _bench.Read(default);

        Assert.Empty(held);
        Assert.Empty(up);
    }

    [Fact]
    public void AHeldOutOfThePlayer_PressesNothingOnThePage()
    {
        _bench.Context = new PadContext(PadMode.Player, PadFocus.Controls);
        var pressed = _bench.Read(PadButtons.A);
        _bench.Context = PadContext.Browsing;

        var held = _bench.Hold(PadButtons.A, Second);
        var up = _bench.Read(default);

        Assert.Equal([new PadAction(PadActionKind.Activate)], pressed);
        Assert.Empty(held);
        Assert.Empty(up);
    }

    [Fact]
    public void APadAtRest_IsIdle()
    {
        _bench.Tap(PadButtons.B);
        _bench.Rest(Second);

        Assert.True(_bench.Pad.IsIdle);
        Assert.False(_bench.Pad.IsActive);
    }

    [Fact]
    public void APadFirstRead_IsNotIdleUntilItsFirstReadingIsTaken()
    {
        var pad = new PadInterpreter(new Fakes.ManualTimeProvider());
        bool before = pad.IsIdle;

        pad.Update(default, PadContext.Browsing);

        Assert.False(before);
        Assert.True(pad.IsIdle);
    }

    [Fact]
    public void ADriftingStick_IsAPadAtRest_APressedButtonIsNot()
    {
        Assert.True(PadInterpreter.IsAtRest(new PadReading(PadButtons.None, LeftX: 0.1, LeftY: 0.2, RightX: -0.2, RightTrigger: 0.1)));
        Assert.False(PadInterpreter.IsAtRest(new PadReading(PadButtons.View)));
        Assert.False(PadInterpreter.IsAtRest(new PadReading(PadButtons.None, RightY: 0.3)));
        Assert.False(PadInterpreter.IsAtRest(new PadReading(PadButtons.None, LeftTrigger: 0.2)));
    }

    [Fact]
    public void TwoPads_CountAsOne()
    {
        var merged = PadReading.Merge(
            new PadReading(PadButtons.A, LeftX: 0.2, RightY: -0.9, LeftTrigger: 0.3),
            new PadReading(PadButtons.DPadLeft, LeftX: -0.8, RightY: 0.1, RightTrigger: 1));

        Assert.Equal(new PadReading(PadButtons.A | PadButtons.DPadLeft, LeftX: -0.8, RightY: -0.9, LeftTrigger: 0.3, RightTrigger: 1), merged);
    }
}
