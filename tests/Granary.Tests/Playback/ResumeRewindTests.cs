using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>Rewind on resume, as Silo's Android app applies it (PlaybackStartPosition.kt, applyResumeRewind).</summary>
public sealed class ResumeRewindTests
{
    [Fact]
    public void ByDefault_AResumeStartsExactlyAtTheSavedPoint() =>
        Assert.Equal(1325.5, ResumeRewind.Apply(1325.5, ResumeRewind.DefaultSeconds));

    [Theory]
    [InlineData(600, 7, 593)]
    [InlineData(1325.5, 20, 1305.5)]
    [InlineData(30, 7, 23)]
    public void AResumeFromHalfAMinuteIn_StartsTheRewindEarlier(double position, int seconds, double start) =>
        Assert.Equal(start, ResumeRewind.Apply(position, seconds));

    [Theory]
    [InlineData(20)]
    [InlineData(29.9)]
    public void AResumeCloserToTheStart_StartsExactlyThere(double position) => Assert.Equal(position, ResumeRewind.Apply(position, 7));

    [Fact]
    public void TheRewind_NeverGoesBeforeTheStart() => Assert.Equal(0, ResumeRewind.Apply(30, ResumeRewind.MaxSeconds));
}
