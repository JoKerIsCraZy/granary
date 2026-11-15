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

    [Fact]
    public void At_KeepsThePositionAndTheChoice()
    {
        var choice = new PrePlayChoice("42", IsVersionPicked: true, AudioTrack: 1, SubtitleTrack: 3);

        var request = PlayRequest.At("movie-1", 4747.284, choice);

        Assert.Equal(("movie-1", 4747.284, choice), (request.ContentId, request.StartSeconds, request.Choice));
        Assert.False(request.FromBeginning);
        Assert.False(request.IsWatchParty);
        Assert.Equal(0, PlayRequest.At("movie-1", 0).StartSeconds);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.5)]
    public void At_RefusesWhatIsNotAPosition(double seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayRequest.At("movie-1", seconds));

    [Fact]
    public void APositionGivenFromOutside_IsNotRewound_AndWinsOverTheSavedPoint() =>
        Assert.Equal(4747.284, StartPosition(At(1325.5), PlayRequest.At("movie-1", 4747.284), rewind: ResumeRewind.MaxSeconds));

    [Fact]
    public void APositionGivenFromOutside_OfATitleOfUnknownLength_IsKeptWithinTheServersRange()
    {
        // No file says how long the title is: a position the server takes is kept, one beyond it resumes.
        Assert.Equal(4747.284, StartPosition(At(1325.5), PlayRequest.At("movie-1", 4747.284), rewind: 0));
        Assert.Equal(1325.5, StartPosition(At(1325.5), PlayRequest.At("movie-1", 31_536_001), rewind: 0));
    }
}
