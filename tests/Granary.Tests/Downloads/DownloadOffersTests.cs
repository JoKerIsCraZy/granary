using Granary.Core.Api.Catalog;
using Granary.Core.Downloads;

namespace Granary.Tests.Downloads;

/// <summary>What a season's or a series' Download starts, in which order the queue takes it, and what Only unwatched leaves out.</summary>
public sealed class DownloadOffersTests
{
    private static readonly WatchRollup Watched = new() { Played = true };
    private static readonly WatchRollup InProgress = new() { IsInProgress = true, PositionSeconds = 600 };

    private static Episode Episode(int season, int number, WatchRollup? watch = null) => new()
    {
        ContentId = $"ep_{season}_{number}",
        SeasonNumber = season,
        EpisodeNumber = number,
        Title = $"Chapter {number}",
        UserData = watch,
        Files = [new EpisodeFile { FileId = $"f_{season}_{number}", FileSize = 1000 * number }],
    };

    private static CatalogItemDetail SeasonOne => new() { SeriesId = "sr_1", SeasonNumber = 1, Title = "Season 1", SeriesTitle = "Example Series" };

    // The tester's case: one to six watched, seven half-watched, eight not yet.
    [Fact]
    public void OnlyUnwatched_LeavesOutTheWatchedEpisodes_AndKeepsTheOneInProgress()
    {
        var offer = DownloadOffers.ForSeason(SeasonOne, [Episode(1, 6, Watched), Episode(1, 7, InProgress), Episode(1, 8)], _ => false)!;

        var unwatched = offer.Unwatched();

        Assert.True(offer.HasWatched);
        Assert.Equal([6, 7, 8], offer.Request.Episodes.Select(e => e.EpisodeNumber));
        Assert.Equal(21000, offer.KnownBytes);
        Assert.Equal([7, 8], unwatched.Request.Episodes.Select(e => e.EpisodeNumber));
        Assert.Equal(15000, unwatched.KnownBytes);
        Assert.False(unwatched.HasWatched);
    }

    [Fact]
    public void ASeasonWithoutWatchedEpisodes_HasNothingForOnlyUnwatchedToLeaveOut()
    {
        var offer = DownloadOffers.ForSeason(SeasonOne, [Episode(1, 1, InProgress), Episode(1, 2)], _ => false)!;

        Assert.False(offer.HasWatched);
        Assert.Equal(offer.Request.Episodes, offer.Unwatched().Request.Episodes);
    }

    // An episode on this PC is not offered, watched or not: it says nothing about what is left.
    [Fact]
    public void AWatchedEpisodeOnThisPc_IsNoWatchedEpisodeLeft()
    {
        var offer = DownloadOffers.ForSeason(SeasonOne, [Episode(1, 1, Watched), Episode(1, 2)], id => id == "ep_1_1")!;

        Assert.False(offer.HasWatched);
    }

    [Fact]
    public void ASeries_OnlyUnwatched_LeavesOutItsWatchedEpisodes_AndTheSeasonsWithNoneLeft()
    {
        var series = new CatalogItemDetail { Title = "Example Series" };
        IReadOnlyList<(Season, IReadOnlyList<Episode>)> seasons =
        [
            (new Season { SeasonNumber = 1 }, [Episode(1, 1, Watched), Episode(1, 2, Watched)]),
            (new Season { SeasonNumber = 2 }, [Episode(2, 1, Watched), Episode(2, 2)]),
        ];

        var offer = DownloadOffers.ForSeries(series, "sr_1", seasons, _ => false)!;
        var unwatched = offer.Unwatched();

        Assert.True(offer.HasWatched);
        Assert.Equal([(2, 2)], unwatched.Request.Seasons.SelectMany(s => s.Episodes.Select(e => (s.SeasonNumber, e.EpisodeNumber))));
        Assert.Equal(2000, unwatched.KnownBytes);
    }

    [Fact]
    public void ASeason_StartsItsEpisodesInTheirOrder_HoweverTheServerListsThem()
    {
        var offer = DownloadOffers.ForSeason(SeasonOne, [Episode(1, 3), Episode(1, 1), Episode(1, 2)], _ => false);

        Assert.Equal([1, 2, 3], offer!.Request.Episodes.Select(e => e.EpisodeNumber));
    }

    [Fact]
    public void ASeries_StartsEachSeasonsEpisodesInTheirOrder_HoweverTheServerListsThem()
    {
        var series = new CatalogItemDetail { Title = "Example Series" };
        IReadOnlyList<(Season, IReadOnlyList<Episode>)> seasons =
        [
            (new Season { SeasonNumber = 2 }, [Episode(2, 2), Episode(2, 1)]),
            (new Season { SeasonNumber = 1 }, [Episode(1, 3), Episode(1, 1), Episode(1, 2)]),
        ];

        var offer = DownloadOffers.ForSeries(series, "sr_1", seasons, _ => false);

        Assert.Equal([(1, 1), (1, 2), (1, 3), (2, 1), (2, 2)],
            offer!.Request.Seasons.SelectMany(s => s.Episodes.Select(e => (s.SeasonNumber, e.EpisodeNumber))));
    }
}
