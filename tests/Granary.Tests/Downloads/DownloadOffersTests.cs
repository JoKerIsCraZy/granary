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
        Runtime = 50,
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

    // The specials, then two seasons: the first watched, the second half-way.
    private static IReadOnlyList<(Season, IReadOnlyList<Episode>)> HalfWatched =>
    [
        (new Season { SeasonNumber = 0, Title = "Specials", IsSpecials = true }, [Episode(0, 1)]),
        (new Season { SeasonNumber = 1, Title = "Season 1" }, [Episode(1, 1, Watched), Episode(1, 2, Watched)]),
        (new Season { SeasonNumber = 2, Title = "Season 2" }, [Episode(2, 1, Watched), Episode(2, 2, InProgress), Episode(2, 3)]),
    ];

    // As the Apple app's series sheet: This Episode and Season N are the ones the series' page plays next.
    [Fact]
    public void ASeries_OffersTheEpisodeItsPlayTakes_AndThatSeason()
    {
        var series = new CatalogItemDetail { Title = "Example Series", PlayContentId = "ep_2_3", PlaySeasonNumber = 2 };

        var offer = DownloadOffers.ForSeries(series, "sr_1", HalfWatched, _ => false)!;

        Assert.Equal(("ep_2_3", "sr_1", "f_2_3", "Chapter 3", "Example Series", 2, 3), (offer.Episode!.Request.MediaItemId, offer.Episode.Request.SeriesId,
            offer.Episode.Request.MediaFileId, offer.Episode.Request.Title, offer.Episode.Request.SeriesTitle, offer.Episode.Request.SeasonNumber,
            offer.Episode.Request.EpisodeNumber));
        Assert.Equal((3000L, 3000), (offer.Episode.Versions.Single().FileSize, offer.Episode.RuntimeSeconds));
        Assert.Equal((2, "Season 2"), (offer.Season!.SeasonNumber, offer.Season.Title));
        Assert.Equal([1, 2, 3], offer.Season.Offer.Request.Episodes.Select(e => e.EpisodeNumber));
        Assert.Equal([2, 3], offer.Season.Offer.Unwatched().Request.Episodes.Select(e => e.EpisodeNumber));
    }

    // Without a next episode from the server, as the Apple app picks one: the one in progress, else the first unwatched,
    // else the first, the specials last.
    [Fact]
    public void WithoutAPlayTarget_ThisEpisodeIsTheOneInProgress_ElseTheFirstUnwatched_ElseTheFirst()
    {
        var series = new CatalogItemDetail { Title = "Example Series" };
        IReadOnlyList<(Season, IReadOnlyList<Episode>)> unwatched =
            [(new Season { SeasonNumber = 1 }, [Episode(1, 1, Watched), Episode(1, 2), Episode(1, 3)])];
        IReadOnlyList<(Season, IReadOnlyList<Episode>)> allWatched =
            [(new Season { SeasonNumber = 0, IsSpecials = true }, [Episode(0, 1, Watched)]), (new Season { SeasonNumber = 1 }, [Episode(1, 1, Watched)])];

        var inProgress = DownloadOffers.ForSeries(series, "sr_1", HalfWatched, _ => false)!;
        var firstUnwatched = DownloadOffers.ForSeries(series, "sr_1", unwatched, _ => false)!;
        var first = DownloadOffers.ForSeries(series, "sr_1", allWatched, _ => false)!;

        Assert.Equal(("ep_2_2", 2), (inProgress.Episode!.Request.MediaItemId, inProgress.Season!.SeasonNumber));
        Assert.Equal(("ep_1_2", 1), (firstUnwatched.Episode!.Request.MediaItemId, firstUnwatched.Season!.SeasonNumber));
        Assert.Equal(("ep_1_1", 1), (first.Episode!.Request.MediaItemId, first.Season!.SeasonNumber));
    }

    // This Episode says when it is on this PC, so it stays offered; a season with nothing left has no episodes to offer.
    [Fact]
    public void ThisEpisodeAndItsSeason_StayOffered_WhenTheyAreOnThisPc()
    {
        var series = new CatalogItemDetail { Title = "Example Series", PlayContentId = "ep_2_3", PlaySeasonNumber = 2 };

        var offer = DownloadOffers.ForSeries(series, "sr_1", HalfWatched, id => id.StartsWith("ep_2_", StringComparison.Ordinal))!;

        Assert.Equal("ep_2_3", offer.Episode!.Request.MediaItemId);
        Assert.Equal(2, offer.Season!.SeasonNumber);
        Assert.Empty(offer.Season.Offer.Request.Episodes);
    }

    [Fact]
    public void ThePlayTargetsSeason_IsTheSeason_ThoughItsEpisodeHasNoFile()
    {
        var series = new CatalogItemDetail { Title = "Example Series", PlayContentId = "ep_2_9", PlaySeasonNumber = 2 };

        var offer = DownloadOffers.ForSeries(series, "sr_1", HalfWatched, _ => false)!;

        Assert.Equal(("ep_2_2", 2), (offer.Episode!.Request.MediaItemId, offer.Season!.SeasonNumber));
    }
}
