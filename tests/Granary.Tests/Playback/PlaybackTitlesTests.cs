using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>The player's two title lines, written as Silo's web player writes them.</summary>
public sealed class PlaybackTitlesTests
{
    [Fact]
    public void Movie_ShowsItsTitleAndYear()
    {
        var detail = new WatchDetail { Type = WatchItemTypes.Movie, Title = "Arrival", Year = 2016 };

        Assert.Equal(("Arrival", "2016"), PlaybackStarter.Titles(detail));
    }

    [Fact]
    public void MovieWithoutAYear_HasNoSecondLine()
    {
        var detail = new WatchDetail { Type = WatchItemTypes.Movie, Title = "Arrival" };

        Assert.Equal(("Arrival", ""), PlaybackStarter.Titles(detail));
    }

    [Fact]
    public void Episode_ShowsTheSeriesThenSeasonEpisodeAndItsTitle()
    {
        var detail = new WatchDetail
        {
            Type = WatchItemTypes.Episode, Title = "Schulanfang", SeriesTitle = "ted", SeasonNumber = 1, EpisodeNumber = 1,
        };

        Assert.Equal(("ted", "S1 · E1 — Schulanfang"), PlaybackStarter.Titles(detail));
    }

    [Fact]
    public void EpisodeWithoutNumbers_ShowsItsTitleAlone()
    {
        var detail = new WatchDetail { Type = WatchItemTypes.Episode, Title = "Special", SeriesTitle = "ted", SeasonNumber = 0 };

        Assert.Equal(("ted", "Special"), PlaybackStarter.Titles(detail));
    }

    [Fact]
    public void EpisodeWithoutItsSeries_TakesTheEpisodeTitleFirst()
    {
        var detail = new WatchDetail { Type = WatchItemTypes.Episode, Title = "Pilot", SeasonNumber = 1, EpisodeNumber = 1 };

        Assert.Equal(("Pilot", "S1 · E1"), PlaybackStarter.Titles(detail));
    }
}
