using Granary.Core.Controller;

namespace Granary.Tests.Controller;

/// <summary>Seeking with a game controller: steps that add up into one seek, and the triggers' scrub.</summary>
public sealed class PadSeekTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly PadReading FullRightTrigger = new(PadButtons.None, RightTrigger: 1);

    // An hour in, of a title of three.
    private readonly PadBench _bench = new(new PadContext(PadMode.Player, CanSeek: true, SkipBackSeconds: 10, SkipForwardSeconds: 30,
        PositionSeconds: 3600, DurationSeconds: 10800));

    private static List<double> Previews(IEnumerable<PadAction> actions) =>
        [.. actions.Where(action => action.Kind == PadActionKind.SeekPreview).Select(action => action.Amount)];

    private static List<PadAction> Commits(IEnumerable<PadAction> actions) =>
        [.. actions.Where(action => action.Kind is PadActionKind.SeekCommit or PadActionKind.SeekCancel)];

    [Fact]
    public void LeftOnThePicture_ShowsWhereItWouldLand_AndSeeksOnceAfterAShortWait()
    {
        var tapped = _bench.Tap(PadButtons.DPadLeft);
        var soon = _bench.Rest(TimeSpan.FromMilliseconds(300));
        var later = _bench.Rest(Second);

        Assert.Equal([PadAction.Of(PadActionKind.SeekPreview, -10)], tapped);
        Assert.Empty(soon);
        Assert.Equal([PadAction.Of(PadActionKind.SeekCommit, -10)], later);
    }

    [Fact]
    public void QuickTaps_AddUpIntoOneSeek()
    {
        var actions = new List<PadAction>();
        for (int tap = 0; tap < 3; tap++)
        {
            actions.AddRange(_bench.Tap(PadButtons.DPadRight));
            actions.AddRange(_bench.Rest(TimeSpan.FromMilliseconds(150)));
        }
        actions.AddRange(_bench.Rest(Second));

        Assert.Equal([30, 60, 90], Previews(actions));
        Assert.Equal([PadAction.Of(PadActionKind.SeekCommit, 90)], Commits(actions));
    }

    [Fact]
    public void TheLeftStick_SeeksAsTheDPadDoes()
    {
        var actions = _bench.Read(new PadReading(PadButtons.None, LeftX: 1));

        Assert.Equal([PadAction.Of(PadActionKind.SeekPreview, 30)], actions);
    }

    [Fact]
    public void AHeldDirection_SeeksInStepsThatGrow_AndSendsNothingUntilLetGo()
    {
        var held = _bench.Hold(PadButtons.DPadLeft, 6 * Second);
        var after = _bench.Rest(Second);

        var previews = Previews(held);
        var steps = previews.Zip(previews.Skip(1), (before, next) => before - next).ToList();
        Assert.Empty(Commits(held));
        Assert.Equal(-10, previews[0]);
        // Never smaller than the step before, and on from the profile's 10 s to half a minute and a minute.
        Assert.Equal(steps.Order(), steps);
        Assert.Contains(10, steps);
        Assert.Contains(30, steps);
        Assert.Contains(60, steps);
        Assert.Contains(180, steps);
        Assert.Equal([PadAction.Of(PadActionKind.SeekCommit, previews[^1])], after);
    }

    [Fact]
    public void BackToWhereItBegan_SeeksNothing()
    {
        var actions = _bench.Tap(PadButtons.DPadRight);
        actions.AddRange(_bench.Tap(PadButtons.DPadLeft));
        actions.AddRange(_bench.Tap(PadButtons.DPadLeft));
        actions.AddRange(_bench.Tap(PadButtons.DPadLeft));
        actions.AddRange(_bench.Rest(Second));

        Assert.Equal([30, 20, 10, 0], Previews(actions));
        Assert.Equal([new PadAction(PadActionKind.SeekCancel)], Commits(actions));
    }

    [Fact]
    public void ASeekPastTheEnd_StopsThere_AndComesBackAtOnce()
    {
        _bench.Context = _bench.Context with { PositionSeconds = 100, DurationSeconds = 160 };

        var actions = _bench.Tap(PadButtons.DPadRight);
        actions.AddRange(_bench.Tap(PadButtons.DPadRight));
        actions.AddRange(_bench.Tap(PadButtons.DPadRight));
        actions.AddRange(_bench.Tap(PadButtons.DPadLeft));

        Assert.Equal([30, 60, 60, 50], Previews(actions));
    }

    [Fact]
    public void AScrubPastTheEnd_SeeksToTheEnd()
    {
        _bench.Context = _bench.Context with { PositionSeconds = 100, DurationSeconds = 160 };

        var held = _bench.Hold(FullRightTrigger, TimeSpan.FromSeconds(2));
        var released = _bench.Read(default);

        Assert.Equal(60, Previews(held)[^1]);
        Assert.Equal([PadAction.Of(PadActionKind.SeekCommit, 60)], released);
    }

    [Fact]
    public void ASeekHeldPastTheStart_StopsAtTheStart()
    {
        _bench.Context = _bench.Context with { PositionSeconds = 25 };

        var actions = _bench.Tap(PadButtons.DPadLeft);
        actions.AddRange(_bench.Tap(PadButtons.DPadLeft));
        actions.AddRange(_bench.Tap(PadButtons.DPadLeft));
        actions.AddRange(_bench.Tap(PadButtons.DPadRight));

        Assert.Equal([-10, -20, -25, 5], Previews(actions));
    }

    [Fact]
    public void ATitleOfUnknownLength_LeavesTheSeeksEndOpen()
    {
        _bench.Context = _bench.Context with { PositionSeconds = 100, DurationSeconds = 0 };

        var held = _bench.Hold(FullRightTrigger, TimeSpan.FromSeconds(1));

        Assert.True(Previews(held)[^1] > 150);
    }

    [Fact]
    public void ATitleThatCannotBeSought_TakesNoSeek()
    {
        _bench.Context = _bench.Context with { CanSeek = false };

        var actions = _bench.Hold(new PadReading(PadButtons.DPadRight, RightTrigger: 1), Second);
        actions.AddRange(_bench.Rest(Second));

        Assert.Empty(actions);
    }

    [Fact]
    public void ATrigger_ScrubsWhileHeld_AndSeeksOnceWhenLetGo()
    {
        var held = _bench.Hold(FullRightTrigger, Second);
        var released = _bench.Read(default);
        var after = _bench.Rest(Second);

        var previews = Previews(held);
        Assert.Empty(Commits(held));
        Assert.Equal(previews.Order(), previews);
        Assert.Equal(PadTuning.ScrubRate(1), previews[^1], PadTuning.ScrubRate(1) * 0.05);
        Assert.Equal([PadAction.Of(PadActionKind.SeekCommit, previews[^1])], released);
        Assert.Empty(after);
    }

    [Fact]
    public void TheLeftTrigger_ScrubsBack()
    {
        var held = _bench.Hold(new PadReading(PadButtons.None, LeftTrigger: 1), Second);

        Assert.True(Previews(held)[^1] < -100);
    }

    [Fact]
    public void ALightPull_ScrubsSlowerThanAFullOne()
    {
        double light = Previews(_bench.Hold(new PadReading(PadButtons.None, RightTrigger: 0.55), Second))[^1];
        _bench.Read(default);
        double full = Previews(_bench.Hold(FullRightTrigger, Second))[^1];

        Assert.True(light > 0);
        Assert.True(light < full / 4);
    }

    [Fact]
    public void ATriggerAPadReportsAsAButton_ScrubsAtFullSpeed()
    {
        var held = _bench.Hold(PadButtons.RightTrigger, Second);

        Assert.Equal(PadTuning.ScrubRate(1), Previews(held)[^1], PadTuning.ScrubRate(1) * 0.05);
    }

    [Fact]
    public void AStepAndAScrub_EndAsOneSeek()
    {
        var actions = _bench.Tap(PadButtons.DPadRight);
        actions.AddRange(_bench.Hold(FullRightTrigger, Second));
        actions.AddRange(_bench.Read(default));
        actions.AddRange(_bench.Rest(Second));

        var commit = Assert.Single(Commits(actions));
        Assert.Equal(PadActionKind.SeekCommit, commit.Kind);
        Assert.True(commit.Amount > 30 + 100);
    }

    [Fact]
    public void AReadingThatComesLate_ScrubsNoFurtherThanAShortStep()
    {
        _bench.Read(FullRightTrigger);

        var late = _bench.Read(FullRightTrigger, after: 5 * Second);

        Assert.True(Previews(late)[^1] <= PadTuning.ScrubRate(1) * 0.15);
    }

    [Fact]
    public void APadThatGoesAwayMidScrub_SeeksNothing()
    {
        _bench.Hold(FullRightTrigger, Second);

        _bench.Pad.Reset();
        var after = _bench.Rest(2 * Second);

        Assert.Empty(after);
        Assert.True(_bench.Pad.IsIdle);
    }

    [Fact]
    public void ASeekThatWaits_KeepsThePadFromIdling()
    {
        _bench.Tap(PadButtons.DPadRight);

        Assert.False(_bench.Pad.IsIdle);
    }

    [Fact]
    public void ThePlayerClosingMidSeek_SendsNoSeek()
    {
        _bench.Tap(PadButtons.DPadRight);
        _bench.Context = PadContext.Browsing;

        Assert.Empty(_bench.Rest(Second));
    }

    [Fact]
    public void EachStep_IsNoSmallerThanTheOneBefore()
    {
        double[] steps = [.. Enumerable.Range(0, 20).Select(half => PadTuning.SeekStep(TimeSpan.FromSeconds(half / 2.0), 10))];

        Assert.Equal(steps.Order(), steps);
        Assert.Equal(10, steps[0]);
        Assert.Equal(300, steps[^1]);
    }
}
