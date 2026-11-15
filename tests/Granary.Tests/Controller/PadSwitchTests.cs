using Granary.Core.Controller;

namespace Granary.Tests.Controller;

/// <summary>
/// The Game controller switch (<see cref="ControllerSetting"/>) turned off or on while a pad is held: the window then
/// starts afresh (<see cref="PadInterpreter.Reset"/>, MainWindow.Controller.cs), so nothing stays held and no repeat goes
/// on. Off, the window reads nothing; on again, what is still held is no press.
/// </summary>
public sealed class PadSwitchTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly PadBench _bench = new();

    [Fact]
    public void ARepeatUnderWay_DoesNotGoOn_AfterTheSwitchWentOffAndOnWithTheDPadStillHeld()
    {
        var repeating = _bench.Hold(PadButtons.DPadDown, Second);
        // Switched off: the window starts afresh and reads nothing more. On again: afresh once more.
        _bench.Pad.Reset();
        _bench.Pad.Reset();

        var stillHeld = _bench.Hold(PadButtons.DPadDown, Second);
        _bench.Read(default);
        var pressedAgain = _bench.Read(PadButtons.DPadDown);

        Assert.Contains(PadAction.Move(PadDirection.Down, isRepeat: true), repeating);
        Assert.Empty(stillHeld);
        Assert.Equal([PadAction.Move(PadDirection.Down)], pressedAgain);
    }

    [Fact]
    public void AHeldA_SwitchedOffAndOnAgain_PressesNothingWhenLetGo()
    {
        var down = _bench.Read(PadButtons.A);
        _bench.Pad.Reset();
        _bench.Pad.Reset();

        var held = _bench.Hold(PadButtons.A, TimeSpan.FromMilliseconds(100));
        var up = _bench.Read(default);

        Assert.Empty(down);
        Assert.Empty(held);
        Assert.Empty(up);
    }

    [Fact]
    public void SwitchedOff_NothingIsHeldOrAwake()
    {
        _bench.Hold(PadButtons.DPadDown | PadButtons.A, Second);

        _bench.Pad.Reset();

        Assert.False(_bench.Pad.IsActive);
    }
}
