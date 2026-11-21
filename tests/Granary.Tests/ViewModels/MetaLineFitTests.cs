using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>How the player's line under the title shares a narrow width between the episode and the chapter.</summary>
public sealed class MetaLineFitTests
{
    [Fact]
    public void WithRoom_BothShowWhole() =>
        Assert.Equal((120, 90), MetaLineFit.Split(episode: 120, chapter: 90, others: 200, room: 500));

    [Fact]
    public void ANarrowerLine_CutsTheChapterFirst_AsTheWebKeepsTheEpisodeWhole() =>
        Assert.Equal((120, 60), MetaLineFit.Split(episode: 120, chapter: 90, others: 200, room: 380));

    [Fact]
    public void AChapterLeftTooLittle_IsLeftOut_AndTheEpisodeKeepsItsRoom() =>
        Assert.Equal((120, 0), MetaLineFit.Split(episode: 120, chapter: 90, others: 200, room: 120 + 200 + MetaLineFit.MinChapterWidth - 1));

    [Fact]
    public void WithoutRoomForTheEpisode_TheEpisodeIsCut_NeverTheTimes() =>
        Assert.Equal((50, 0), MetaLineFit.Split(episode: 120, chapter: 90, others: 200, room: 250));

    [Fact]
    public void LessRoomThanTheTimes_LeavesBothOut() =>
        Assert.Equal((0, 0), MetaLineFit.Split(episode: 120, chapter: 90, others: 200, room: 150));

    // The times and the end clock never reach past the line: with too little room the end clock goes, then the times are cut.
    [Fact]
    public void TimesAndEndClockThatFit_ShowWhole() =>
        Assert.Equal([150, 90], MetaLineFit.FitOthers([150, 90], room: 240));

    [Fact]
    public void NoRoomForTheEndClock_LeavesItOut_AndKeepsTheTimesWhole() =>
        Assert.Equal([150, 0], MetaLineFit.FitOthers([150, 90], room: 200));

    [Fact]
    public void NoRoomForTheTimes_CutsThemToTheLine() =>
        Assert.Equal([120, 0], MetaLineFit.FitOthers([150, 90], room: 120));

    [Fact]
    public void WithoutAChapter_TheEpisodeTakesTheRoom() =>
        Assert.Equal((80, 0), MetaLineFit.Split(episode: 120, chapter: 0, others: 200, room: 280));
}
