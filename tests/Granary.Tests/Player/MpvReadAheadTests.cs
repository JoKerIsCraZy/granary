using System.Globalization;
using Granary.Core.Playback;
using Granary.Player;

namespace Granary.Tests.Player;

/// <summary>The read-ahead buffer as mpv's cache options for each title, and the viewer's mpv.conf over them.</summary>
public sealed class MpvReadAheadTests
{
    private const long MiB = 1024 * 1024;

    private static readonly HashSet<string> NoFile = [];

    private static string Bytes(long miB) => (miB * MiB).ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void TheDefault_Reads384MiBAhead_AndKeeps64MiBBehind()
    {
        Assert.Equal(
        [
            new("cache", "yes"), new("demuxer-max-bytes", Bytes(384)), new("demuxer-max-back-bytes", Bytes(64)),
            new("demuxer-readahead-secs", "120"),
        ], MpvReadAhead.Options(ReadAheadBuffer.Default, NoFile));
    }

    [Theory]
    [InlineData(ReadAheadBuffer.Default, 384, 64)]
    [InlineData(ReadAheadBuffer.Mb150, 150, 50)]
    [InlineData(ReadAheadBuffer.Mb768, 768, 128)]
    [InlineData(ReadAheadBuffer.Gb1, 1024, 192)]
    [InlineData(ReadAheadBuffer.Gb2, 2048, 256)]
    public void EachChoice_GivesItsSizes_WithTheCacheOn(ReadAheadBuffer buffer, long aheadMiB, long behindMiB)
    {
        var options = MpvReadAhead.Options(buffer, NoFile).ToDictionary();

        Assert.Equal("yes", options["cache"]);
        Assert.Equal(Bytes(aheadMiB), options["demuxer-max-bytes"]);
        Assert.Equal(Bytes(behindMiB), options["demuxer-max-back-bytes"]);
        Assert.Equal("120", options["demuxer-readahead-secs"]);
    }

    [Fact]
    public void WhatTheViewersMpvConfSets_IsLeftToIt()
    {
        var options = MpvReadAhead.Options(ReadAheadBuffer.Gb1, new HashSet<string> { "demuxer-max-bytes", "cache" });

        Assert.Equal([new("demuxer-max-back-bytes", Bytes(192)), new("demuxer-readahead-secs", "120")], options);
    }

    [Fact]
    public void TheNamesAMpvConfMaySet_AreTheOptionsTheBufferSets() =>
        Assert.Equal(MpvReadAhead.Names.Order(), MpvReadAhead.Options(ReadAheadBuffer.Default, NoFile).Select(o => o.Key).Order());

    [Fact]
    public void EachChoice_HasItsOwnName_AndAnUnknownNameIsTheDefault()
    {
        Assert.Equal(ReadAheadBuffers.All, ReadAheadBuffers.All.Select(b => ReadAheadBuffers.FromName(ReadAheadBuffers.Name(b))));
        Assert.Equal(ReadAheadBuffer.Default, ReadAheadBuffers.FromName("3gb"));
        Assert.Equal(ReadAheadBuffer.Default, ReadAheadBuffers.FromName(null));
    }
}
