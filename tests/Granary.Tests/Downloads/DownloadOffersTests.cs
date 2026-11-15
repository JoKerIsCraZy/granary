using Granary.Core.Api.Catalog;
using Granary.Core.Downloads;

namespace Granary.Tests.Downloads;

/// <summary>What a season's or a series' Download starts, and in which order the queue takes it.</summary>
public sealed class DownloadOffersTests
{
    private static Episode Episode(int season, int number) => new()
    {
        ContentId = $"ep_{season}_{number}",
        SeasonNumber = season,
        EpisodeNumber = number,
        Title = $"Chapter {number}",
        Files = [new EpisodeFile { FileId = $"f_{season}_{number}", FileSize = 1000 }],
    };

    [Fact]
    public void ASeason_StartsItsEpisodesInTheirOrder_HoweverTheServerListsThem()
    {
        var season = new CatalogItemDetail { SeriesId = "sr_1", SeasonNumber = 1, Title = "Season 1", SeriesTitle = "Example Series" };

        var offer = DownloadOffers.ForSeason(season, [Episode(1, 3), Episode(1, 1), Episode(1, 2)], _ => false);

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
