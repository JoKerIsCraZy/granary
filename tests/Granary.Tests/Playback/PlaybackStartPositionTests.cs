using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>The start's <c>start_position</c> (docs/api-contract.md, 10.5), with this PC's Rewind on resume.</summary>
public sealed class PlaybackStartPositionTests
{
    private readonly ServerPlaybackFixture _fixture = new();

    private double? StartPosition(WatchUserData? progress, PlayRequest request, int rewind = 7)
    {
        _fixture.DevicePreferences.ResumeRewindSeconds = rewind;
        return _fixture.CreateStarter().StartPosition(new WatchDetail { ContentId = "movie-1", UserData = progress }, request);
    }

    private static WatchUserData At(double position) => new() { PositionSeconds = position, IsInProgress = true };

    [Fact]
    public void WithoutProgress_TheServerDecides() => Assert.Null(StartPosition(null, PlayRequest.Resume("movie-1")));

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void APositionThatIsNoResumePoint_LeavesItToTheServer(double position) =>
        Assert.Null(StartPosition(At(position), PlayRequest.Resume("movie-1")));

    [Fact]
    public void StartOver_IsZero_WhateverTheRewind() =>
        Assert.Equal(0, StartPosition(At(1325.5), PlayRequest.FromStart("movie-1"), rewind: ResumeRewind.MaxSeconds));

    [Theory]
    [InlineData(29.9, 29.9)]
    [InlineData(30, 23)]
    [InlineData(30.5, 23.5)]
    public void ARewind_StartsFromHalfAMinuteIn(double position, double start) =>
        Assert.Equal(start, StartPosition(At(position), PlayRequest.Resume("movie-1")));

    [Fact]
    public void WithoutARewind_AResumeIsExact() => Assert.Equal(1325.5, StartPosition(At(1325.5), PlayRequest.Resume("movie-1"), rewind: 0));

    [Fact]
    public void AChangeOfEpisode_ResumesExactly_WhateverTheRewind() =>
        Assert.Equal(1325.5, StartPosition(At(1325.5), PlayRequest.ResumeWithoutRewind("episode-1-4"), rewind: ResumeRewind.MaxSeconds));
}
