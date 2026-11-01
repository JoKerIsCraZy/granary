using Granary.Core.Playback;
using Granary.Player;

namespace Granary.Tests.Player;

/// <summary>mpv's <c>demuxer-cache-state</c> as its string form gives it: the node written as compact JSON.</summary>
public sealed class MpvCacheStateTests
{
    // As mpv 0.41 writes it after a seek ahead: two ranges, the later one first.
    private const string AfterASeek =
        """{"cache-end":118.250000,"reader-pts":96.000000,"cache-duration":22.250000,"eof":false,"underrun":false,"idle":false,"total-bytes":48210944,"fw-bytes":9437184,"raw-input-rate":5242880,"debug-low-level-seeks":2,"debug-byte-level-seeks":1,"debug-ts-last":118.250000,"ts-per-stream":[{"type":"video","cache-duration":22.250000,"reader-pts":96.000000,"cache-end":118.250000},{"type":"audio","cache-duration":22.300000,"reader-pts":96.000000,"cache-end":118.300000}],"bof-cached":true,"eof-cached":false,"seekable-ranges":[{"start":95.958000,"end":118.166000},{"start":0.000000,"end":41.708000}]}""";

    [Fact]
    public void TheSeekableRanges_AreWhatThePlayerHolds()
    {
        var ranges = MpvCacheState.SeekableRanges(AfterASeek);

        Assert.Equal([new BufferedRange(0, 41.708), new BufferedRange(95.958, 118.166)], ranges);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"eof":false,"idle":true}""")]
    [InlineData("""{"seekable-ranges":[]}""")]
    public void WithoutRanges_NothingIsHeld(string? json)
    {
        Assert.Same(BufferedRanges.None, MpvCacheState.SeekableRanges(json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"seekable-ranges":[{"start":0.000000,"end":""")]
    [InlineData("""{"seekable-ranges":[{"start":0,"end":5}]""")]
    [InlineData("""{"seekable-ranges":[{"start":0,"end":5}]}trailing""")]
    [InlineData("""[{"start":0,"end":5}]""")]
    [InlineData("""{"seekable-ranges":{"start":0,"end":5}}""")]
    [InlineData("""{"seekable-ranges":"0-5"}""")]
    [InlineData("null")]
    [InlineData("42")]
    public void BadData_HoldsNothing(string json)
    {
        Assert.Same(BufferedRanges.None, MpvCacheState.SeekableRanges(json));
    }

    [Fact]
    public void ARangeWithoutTwoTimes_IsLeftOut()
    {
        // mpv quotes a time that is no number ("inf", "nan").
        const string json =
            """{"seekable-ranges":[{"start":0},{"end":5},{"start":"inf","end":9},{"start":10,"end":"nan"},{"start":true,"end":12},5,null,[1,2],{"start":20.5,"end":30}]}""";

        var ranges = MpvCacheState.SeekableRanges(json);

        Assert.Equal([new BufferedRange(20.5, 30)], ranges);
    }

    [Fact]
    public void FieldsMpvAddsLater_DoNotGetInTheWay()
    {
        const string json = """{"new-field":{"nested":[1,{"x":null}]},"seekable-ranges":[{"start":1,"end":2,"extra":"x"}],"after":[]}""";

        Assert.Equal([new BufferedRange(1, 2)], MpvCacheState.SeekableRanges(json));
    }

    [Fact]
    public void FarMoreRangesThanMpvKeeps_AreCutOff()
    {
        // mpv keeps a handful; a list this long is not one of its cache states, and the seek bar draws an element for each.
        string ranges = string.Join(",", Enumerable.Range(0, 200).Select(i => $$"""{"start":{{i * 2}},"end":{{i * 2 + 1}}}"""));

        var held = MpvCacheState.SeekableRanges($$"""{"seekable-ranges":[{{ranges}}]}""");

        Assert.Equal(MpvCacheState.MaxRanges, held.Count);
        Assert.Equal(new BufferedRange(0, 1), held[0]);
    }
}
