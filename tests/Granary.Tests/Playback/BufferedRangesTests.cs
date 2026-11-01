using Granary.Core.Playback;

namespace Granary.Tests.Playback;

public sealed class BufferedRangesTests
{
    [Fact]
    public void Ranges_AreSortedByStart_AndOverlapsJoined()
    {
        // mpv lists its ranges in any order, and two overlap for a bit before it joins them.
        var ranges = BufferedRanges.Of([new(300, 360), new(0, 42), new(40, 90), new(90, 95)]);

        Assert.Equal([new BufferedRange(0, 95), new BufferedRange(300, 360)], ranges);
    }

    [Fact]
    public void EmptyAndBrokenRanges_AreLeftOut()
    {
        var ranges = BufferedRanges.Of(
        [
            new(10, 10), new(20, 15), new(double.NaN, 5), new(0, double.PositiveInfinity), new(double.NegativeInfinity, 3), new(1, 2),
        ]);

        Assert.Equal([new BufferedRange(1, 2)], ranges);
    }

    [Fact]
    public void NoRanges_AreNone()
    {
        Assert.Same(BufferedRanges.None, BufferedRanges.Of([]));
        Assert.Same(BufferedRanges.None, BufferedRanges.Of([new(5, 5)]));
        Assert.Empty(BufferedRanges.None);
    }

    [Fact]
    public void Ranges_CompareByWhatTheyHold()
    {
        var ranges = BufferedRanges.Of([new(0, 5), new(10, 20)]);

        Assert.Equal(BufferedRanges.Of([new(10, 20), new(0, 5)]), ranges);
        Assert.Equal(BufferedRanges.Of([new(10, 20), new(0, 5)]).GetHashCode(), ranges.GetHashCode());
        Assert.NotEqual(BufferedRanges.Of([new(0, 5), new(10, 21)]), ranges);
        Assert.NotEqual(BufferedRanges.None, ranges);
        // A snapshot with the same ranges is the same snapshot.
        Assert.Equal(PlaybackSnapshot.Idle with { Buffered = ranges }, PlaybackSnapshot.Idle with { Buffered = BufferedRanges.Of([new(0, 5), new(10, 20)]) });
    }

    [Fact]
    public void OnTheTimeline_RangesMoveByTheOffset_AsThePositionDoes()
    {
        // A stream that starts at 120 s of the title (docs/api-contract.md, 8.6): source = player + offset.
        var ranges = BufferedRanges.Of([new(0, 30), new(60, 90)]).OnTimeline(offsetSeconds: 120, durationSeconds: 5400);

        Assert.Equal([new BufferedRange(120, 150), new BufferedRange(180, 210)], ranges);
    }

    [Fact]
    public void OnTheTimeline_RangesKeepWithinTheTitle()
    {
        var ranges = BufferedRanges.Of([new(-2, 10), new(50, 70), new(80, 90)]).OnTimeline(offsetSeconds: 0, durationSeconds: 60);

        Assert.Equal([new BufferedRange(0, 10), new BufferedRange(50, 60)], ranges);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void WithoutAKnownLength_NothingIsOnTheTimeline(double duration)
    {
        Assert.Same(BufferedRanges.None, BufferedRanges.Of([new(0, 10)]).OnTimeline(0, duration));
    }

    [Fact]
    public void AnOffsetThatIsNoNumber_PutsNothingOnTheTimeline()
    {
        Assert.Same(BufferedRanges.None, BufferedRanges.Of([new(0, 10)]).OnTimeline(double.NaN, 60));
    }

    [Fact]
    public void RangesAlreadyOnTheTimeline_AreTakenAsTheyAre()
    {
        // No copy four times a second for a file that plays as it is.
        var ranges = BufferedRanges.Of([new(0, 10), new(20, 30)]);

        Assert.Same(ranges, ranges.OnTimeline(0, 60));
    }
}
