using Granary.Core.Abstractions;

namespace Granary.Tests.Playback;

/// <summary>The power requests Granary holds for what the player keeps awake, and how it goes from one state to another.</summary>
public sealed class KeepAwakeRequestsTests
{
    private static readonly PowerRequest VideoDisplay = new(KeepAwakeRequests.VideoReason, PowerRequestType.DisplayRequired);
    private static readonly PowerRequest VideoSystem = new(KeepAwakeRequests.VideoReason, PowerRequestType.SystemRequired);
    private static readonly PowerRequest AudioExecution = new(KeepAwakeRequests.AudioReason, PowerRequestType.ExecutionRequired);
    private static readonly PowerRequest AudioSystem = new(KeepAwakeRequests.AudioReason, PowerRequestType.SystemRequired);

    [Fact]
    public void TheReasons_AreWhatPowercfgShows()
    {
        Assert.Equal("Playing video", KeepAwakeRequests.VideoReason);
        Assert.Equal("Playing audio", KeepAwakeRequests.AudioReason);
        Assert.Equal([KeepAwakeRequests.VideoReason, KeepAwakeRequests.AudioReason], KeepAwakeRequests.Reasons);
    }

    [Fact]
    public void APicture_HoldsTheDisplayAndTheSystem_ForVideo() =>
        Assert.Equal([VideoDisplay, VideoSystem], KeepAwakeRequests.For(KeepAwakeState.Display));

    [Fact]
    public void Sound_HoldsTheProcessAndTheSystem_ForAudio() =>
        Assert.Equal([AudioExecution, AudioSystem], KeepAwakeRequests.For(KeepAwakeState.System));

    [Fact]
    public void Nothing_HoldsNothing() => Assert.Empty(KeepAwakeRequests.For(KeepAwakeState.None));

    [Fact]
    public void TheTypes_AreWindowsOwnNumbers()
    {
        // POWER_REQUEST_TYPE in winbase.h.
        Assert.Equal(0, (int)PowerRequestType.DisplayRequired);
        Assert.Equal(1, (int)PowerRequestType.SystemRequired);
        Assert.Equal(2, (int)PowerRequestType.AwayModeRequired);
        Assert.Equal(3, (int)PowerRequestType.ExecutionRequired);
    }

    [Fact]
    public void FromVideoToAudio_TheNewAreSet_AndTheOldCleared()
    {
        var (set, clear) = KeepAwakeRequests.Changes([VideoDisplay, VideoSystem], KeepAwakeState.System);

        Assert.Equal([AudioExecution, AudioSystem], set);
        Assert.Equal([VideoDisplay, VideoSystem], clear);
    }

    [Fact]
    public void FromNothing_AllAreSet()
    {
        var (set, clear) = KeepAwakeRequests.Changes([], KeepAwakeState.Display);

        Assert.Equal([VideoDisplay, VideoSystem], set);
        Assert.Empty(clear);
    }

    [Fact]
    public void ToNothing_AllAreCleared()
    {
        var (set, clear) = KeepAwakeRequests.Changes([AudioExecution, AudioSystem], KeepAwakeState.None);

        Assert.Empty(set);
        Assert.Equal([AudioExecution, AudioSystem], clear);
    }

    [Fact]
    public void APassAfterOneThatStopped_ChangesOnlyWhatIsMissingOrLeftOver()
    {
        // Audio's execution request was set before its system request failed; the video's are still set.
        var (set, clear) = KeepAwakeRequests.Changes([VideoDisplay, VideoSystem, AudioExecution], KeepAwakeState.System);

        Assert.Equal([AudioSystem], set);
        Assert.Equal([VideoDisplay, VideoSystem], clear);
    }

    [Fact]
    public void WhatIsHeldAlready_ChangesNothing()
    {
        var (set, clear) = KeepAwakeRequests.Changes([VideoSystem, VideoDisplay], KeepAwakeState.Display);

        Assert.Empty(set);
        Assert.Empty(clear);
    }
}
