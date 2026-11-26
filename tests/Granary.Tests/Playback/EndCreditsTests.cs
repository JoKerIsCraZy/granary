using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>Where a file's end credits start, as Silo Server finds them to mark a title watched there.</summary>
public sealed class EndCreditsTests
{
    [Fact]
    public void TheLastCreditsInTheTailWindow_AreTheEndCredits()
    {
        (string, double)[] markers = [("credits", 1_200), ("credits", 2_200), ("credits", 2_500), ("intro", 2_550)];

        Assert.Equal(2_500, EndCredits.Start(markers, 2_600, isEpisode: true));
    }

    [Fact]
    public void AnEpisodesWindow_IsItsLast450sOr40Percent_AFilmsItsLast900sOr25Percent()
    {
        (string, double)[] late = [("credits", 6_500)];
        Assert.Null(EndCredits.Start(late, 7_200, isEpisode: true));
        Assert.Equal(6_500, EndCredits.Start(late, 7_200, isEpisode: false));

        (string, double)[] shortFile = [("credits", 700)];
        Assert.Equal(700, EndCredits.Start(shortFile, 1_000, isEpisode: true));
        Assert.Null(EndCredits.Start(shortFile, 1_000, isEpisode: false));
    }

    [Fact]
    public void CreditsOutsideTheWindow_OrAtTheEnd_OrWithoutALength_AreNone()
    {
        Assert.Null(EndCredits.Start([("credits", 1_900)], 2_400, isEpisode: true));
        Assert.Null(EndCredits.Start([("credits", 2_400)], 2_400, isEpisode: true));
        Assert.Null(EndCredits.Start([("credits", 2_000)], 0, isEpisode: true));
        Assert.Null(EndCredits.Start([("credits", 2_000)], double.NaN, isEpisode: true));
        Assert.Null(EndCredits.Start([], 2_400, isEpisode: true));
    }

    [Fact]
    public void ACreditsMarkerThatStartsTheWindow_Counts()
    {
        Assert.Equal(1_950, EndCredits.Start([("credits", 1_950)], 2_400, isEpisode: true));
    }
}
