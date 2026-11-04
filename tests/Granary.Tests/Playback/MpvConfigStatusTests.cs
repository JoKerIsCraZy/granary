using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>The player's report of how the viewer's mpv.conf fared this run.</summary>
public sealed class MpvConfigStatusTests
{
    [Fact]
    public void AReport_ReplacesThePreviousState()
    {
        var status = new MpvConfigStatus();
        Assert.Equal(MpvConfigState.None, status.Current);

        var used = new MpvConfigState(MpvConfigUse.Used, KeptOptions: 2);
        status.Report(used);
        Assert.Equal(used, status.Current);

        var stopped = new MpvConfigState(MpvConfigUse.StoppedDuringPlayback, "it keeps changing idle");
        status.Report(stopped);
        Assert.Equal(stopped, status.Current);
    }

    [Fact]
    public void ANullReport_IsRejected_WithoutDiscardingTheLastState()
    {
        var status = new MpvConfigStatus();
        var state = new MpvConfigState(MpvConfigUse.NotUsed, "it cannot be read");
        status.Report(state);

        Assert.Throws<ArgumentNullException>(() => status.Report(null!));
        Assert.Equal(state, status.Current);
    }
}
