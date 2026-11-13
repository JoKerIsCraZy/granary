using Granary.Core.Controller;

namespace Granary.Tests.Controller;

/// <summary>
/// Where a stick's rest, a stick's direction, a trigger's press and the long press begin and end: each of
/// <see cref="PadTuning"/>'s numbers exactly at its edge and just beside it.
/// </summary>
public sealed class PadThresholdTests
{
    private static readonly TimeSpan Moment = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan HalfASecond = TimeSpan.FromMilliseconds(500);

    private readonly PadBench _bench = new();

    private static PadReading LeftStick(PadDirection direction, double push) => direction switch
    {
        PadDirection.Up => new PadReading(PadButtons.None, LeftY: push),
        PadDirection.Down => new PadReading(PadButtons.None, LeftY: -push),
        PadDirection.Left => new PadReading(PadButtons.None, LeftX: -push),
        _ => new PadReading(PadButtons.None, LeftX: push),
    };

    private static PadReading Trigger(bool isRight, double pull) =>
        isRight ? new PadReading(PadButtons.None, RightTrigger: pull) : new PadReading(PadButtons.None, LeftTrigger: pull);

    // The left stick's dead zone is a circle of 0.24: on a diagonal it ends where both axes read just under 0.17.
    [Theory]
    [InlineData(0.24, 0)]
    [InlineData(0, -0.24)]
    [InlineData(0.169, 0.169)]
    [InlineData(-0.169, 0.169)]
    public void TheLeftStick_InsideItsDeadZonesCircleOrOnItsEdge_IsAtRest(double x, double y)
    {
        var reading = new PadReading(PadButtons.None, LeftX: x, LeftY: y);

        var actions = _bench.Read(reading);

        Assert.True(PadInterpreter.IsAtRest(reading));
        Assert.False(_bench.Pad.IsActive);
        Assert.Empty(actions);
    }

    [Theory]
    [InlineData(0.241, 0)]
    [InlineData(0, -0.241)]
    public void TheLeftStick_JustPastItsDeadZone_IsNotAtRest(double x, double y)
    {
        var reading = new PadReading(PadButtons.None, LeftX: x, LeftY: y);

        _bench.Read(reading);

        Assert.False(PadInterpreter.IsAtRest(reading));
        Assert.True(_bench.Pad.IsActive);
    }

    // Each axis alone is inside the dead zone, the stick is not: a square dead zone would call it at rest.
    [Theory]
    [InlineData(0.17, 0.17)]
    [InlineData(-0.17, -0.17)]
    [InlineData(0.2, 0.2)]
    public void TheLeftStick_PastItsDeadZonesCircleOnADiagonal_IsNotAtRest_ThoughEachAxisAloneIs(double x, double y)
    {
        var reading = new PadReading(PadButtons.None, LeftX: x, LeftY: y);

        _bench.Read(reading);

        Assert.False(PadInterpreter.IsAtRest(reading));
        Assert.True(_bench.Pad.IsActive);
        Assert.True(PadInterpreter.IsAtRest(new PadReading(PadButtons.None, LeftX: x)));
        Assert.True(PadInterpreter.IsAtRest(new PadReading(PadButtons.None, LeftY: y)));
    }

    // The right stick's circle is 0.265: its diagonal ends between 0.187 and 0.188.
    [Theory]
    [InlineData(0.265, 0, true)]
    [InlineData(0.266, 0, false)]
    [InlineData(0, 0.265, true)]
    [InlineData(0, 0.266, false)]
    [InlineData(0.187, -0.187, true)]
    [InlineData(0.188, -0.188, false)]
    public void TheRightStick_IsAtRestUpToItsOwnWiderDeadZone(double x, double y, bool isAtRest)
    {
        var reading = new PadReading(PadButtons.None, RightX: x, RightY: y);

        _bench.Read(reading);

        Assert.Equal(isAtRest, PadInterpreter.IsAtRest(reading));
        Assert.Equal(!isAtRest, _bench.Pad.IsActive);
    }

    [Theory]
    [InlineData(0.241)]
    [InlineData(0.25)]
    [InlineData(0.265)]
    public void ThePushThatWakesTheLeftStick_LeavesTheRightOneAtRest(double push)
    {
        Assert.False(PadInterpreter.IsAtRest(new PadReading(PadButtons.None, LeftX: push)));
        Assert.True(PadInterpreter.IsAtRest(new PadReading(PadButtons.None, RightX: push)));
    }

    [Fact]
    public void TheRightStick_ScrollsNothingAtItsDeadZonesEdge_AndScrollsJustPastIt()
    {
        var atEdge = _bench.Read(new PadReading(PadButtons.None, RightY: PadTuning.RightStickDeadZone));
        var past = _bench.Read(new PadReading(PadButtons.None, RightY: 0.266));

        Assert.Empty(atEdge);
        var scroll = Assert.Single(past);
        Assert.Equal(PadActionKind.Scroll, scroll.Kind);
        Assert.True(scroll.Y > 0);
    }

    [Fact]
    public void AStillHeldAtHalfASecond_OpensTheMenu_AndNotAMomentBefore()
    {
        _bench.Read(PadButtons.A);

        var justUnder = _bench.Read(new PadReading(PadButtons.A), after: HalfASecond - Moment);
        var atHalfASecond = _bench.Read(new PadReading(PadButtons.A), after: Moment);

        Assert.Empty(justUnder);
        Assert.Equal([new PadAction(PadActionKind.ContextMenu)], atHalfASecond);
    }

    [Fact]
    public void ALetGoJustUnderHalfASecond_Presses()
    {
        _bench.Read(PadButtons.A);
        _bench.Read(new PadReading(PadButtons.A), after: HalfASecond - (2 * Moment));

        var up = _bench.Read(default, after: Moment);

        Assert.Equal([new PadAction(PadActionKind.Activate)], up);
    }

    // The menu opens when A is still down at half a second; a release first seen then is still a press.
    [Fact]
    public void ALetGoAtHalfASecond_Presses()
    {
        _bench.Read(PadButtons.A);

        var up = _bench.Read(default, after: HalfASecond);

        Assert.Equal([new PadAction(PadActionKind.Activate)], up);
    }

    [Theory]
    [InlineData(PadDirection.Up)]
    [InlineData(PadDirection.Down)]
    [InlineData(PadDirection.Left)]
    [InlineData(PadDirection.Right)]
    public void TheStick_IsADirectionAtSevenTenths_AndNotJustUnder(PadDirection direction)
    {
        var justUnder = _bench.Read(LeftStick(direction, 0.699));
        var atSevenTenths = _bench.Read(LeftStick(direction, PadTuning.StickPress));

        Assert.Empty(justUnder);
        Assert.Equal([PadAction.Move(direction)], atSevenTenths);
    }

    [Theory]
    [InlineData(PadDirection.Up)]
    [InlineData(PadDirection.Down)]
    [InlineData(PadDirection.Left)]
    [InlineData(PadDirection.Right)]
    public void TheStick_StaysADirectionAtAHalf_AndLetsGoJustUnder(PadDirection direction)
    {
        _bench.Read(LeftStick(direction, PadTuning.StickPress));

        var atAHalf = _bench.Read(LeftStick(direction, PadTuning.StickRelease));
        // Never let go: pushed on, it is the same push and no new step.
        var pushedOn = _bench.Read(LeftStick(direction, PadTuning.StickPress));
        _bench.Read(LeftStick(direction, 0.499));
        var pushedAgain = _bench.Read(LeftStick(direction, PadTuning.StickPress));

        Assert.Empty(atAHalf);
        Assert.Empty(pushedOn);
        Assert.Equal([PadAction.Move(direction)], pushedAgain);
    }

    [Theory]
    [InlineData(false, PadActionKind.PageUp)]
    [InlineData(true, PadActionKind.PageDown)]
    public void ATrigger_IsPressedAtHalfWay_AndNotJustUnder(bool isRight, PadActionKind expected)
    {
        var justUnder = _bench.Read(Trigger(isRight, 0.499));
        var atHalfWay = _bench.Read(Trigger(isRight, PadTuning.TriggerPress));

        Assert.Empty(justUnder);
        Assert.Equal([new PadAction(expected)], atHalfWay);
    }

    [Theory]
    [InlineData(false, PadActionKind.PageUp)]
    [InlineData(true, PadActionKind.PageDown)]
    public void ATrigger_StaysPressedJustOverThreeTenths_AndIsLetGoAtThem(bool isRight, PadActionKind expected)
    {
        _bench.Read(Trigger(isRight, PadTuning.TriggerPress));

        var justOver = _bench.Read(Trigger(isRight, 0.301));
        // Never let go: pulled on, it is the same pull and pages no second time yet.
        var pulledOn = _bench.Read(Trigger(isRight, PadTuning.TriggerPress));
        _bench.Read(Trigger(isRight, PadTuning.TriggerRelease));
        var pulledAgain = _bench.Read(Trigger(isRight, PadTuning.TriggerPress));

        Assert.Empty(justOver);
        Assert.Empty(pulledOn);
        Assert.Equal([new PadAction(expected)], pulledAgain);
    }
}
