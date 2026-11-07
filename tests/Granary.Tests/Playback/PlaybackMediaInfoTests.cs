using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>What Windows' media controls show for a title: its lines and its picture.</summary>
public sealed class PlaybackMediaInfoTests
{
    private static readonly Uri Server = new("https://silo.example/");

    [Fact]
    public void AMovie_ShowsItsTitleOverItsYear_AndItsPoster()
    {
        var info = PlaybackMediaInfo.Of(new WatchDetail
        {
            Type = WatchItemTypes.Movie,
            Title = "Heat",
            Year = 1995,
            PosterUrl = "/api/v2/artwork/a/poster.webp?sig=x",
            BackdropUrl = "/api/v2/artwork/a/backdrop.webp?sig=y",
        }, Server);

        Assert.Equal(("Heat", "1995", new Uri("https://silo.example/api/v2/artwork/a/poster.webp?sig=x")), (info.Title, info.Subtitle, info.Artwork));
    }

    [Fact]
    public void AnEpisode_ShowsItsOwnTitle_OverItsSeriesAndNumber()
    {
        var info = PlaybackMediaInfo.Of(new WatchDetail
        {
            Type = WatchItemTypes.Episode,
            Title = "Past and Present",
            SeriesTitle = "Dark",
            SeasonNumber = 1,
            EpisodeNumber = 5,
            PosterUrl = "https://cdn.example/still.webp",
        }, Server);

        Assert.Equal(("Past and Present", "Dark — S1 · E5", new Uri("https://cdn.example/still.webp")), (info.Title, info.Subtitle, info.Artwork));
    }

    [Fact]
    public void AnEpisodeWithoutATitle_IsNamedByItsNumber()
    {
        var info = PlaybackMediaInfo.Of(new WatchDetail { Type = WatchItemTypes.Episode, SeriesTitle = "Dark", SeasonNumber = 2, EpisodeNumber = 1 }, Server);

        Assert.Equal(("Episode 1", "Dark — S2 · E1"), (info.Title, info.Subtitle));
    }

    [Fact]
    public void AnEpisodeWithoutItsSeries_ShowsItsNumberAlone()
    {
        var info = PlaybackMediaInfo.Of(new WatchDetail { Type = WatchItemTypes.Episode, Title = "Pilot", SeasonNumber = 1, EpisodeNumber = 1 }, Server);

        Assert.Equal(("Pilot", "S1 · E1"), (info.Title, info.Subtitle));
    }

    [Fact]
    public void AMovie_CarriesItsYear_AndItsOwnIdToFindItOnTmdb()
    {
        var info = PlaybackMediaInfo.Of(new WatchDetail { ContentId = "movie-heat", Type = WatchItemTypes.Movie, Title = "Heat", Year = 1995 }, Server);

        Assert.Equal((PlaybackMediaKind.Movie, 1995, "movie-heat", null, null), (info.Kind, info.Year, info.CatalogId, info.SeriesTitle, info.EpisodeNumber));
    }

    [Fact]
    public void AnEpisode_CarriesItsSeriesNumbersAndTitle_AndItsSeriesIdToFindItOnTmdb()
    {
        var info = PlaybackMediaInfo.Of(new WatchDetail
        {
            ContentId = "episode-dark-1-5",
            Type = WatchItemTypes.Episode,
            Title = "Past and Present",
            SeriesId = "series-dark",
            SeriesTitle = "Dark",
            SeasonNumber = 1,
            EpisodeNumber = 5,
            Year = 2017,
        }, Server);

        Assert.Equal((PlaybackMediaKind.Episode, "Dark", 1, 5, "Past and Present", "series-dark"),
            (info.Kind, info.SeriesTitle, info.SeasonNumber, info.EpisodeNumber, info.EpisodeTitle, info.CatalogId));
    }

    [Fact]
    public void AnEpisodeWithoutATitle_HasNoEpisodeTitle_ThoughItsLineNamesItByNumber()
    {
        var info = PlaybackMediaInfo.Of(new WatchDetail { Type = WatchItemTypes.Episode, Title = " ", SeriesId = "series-dark", SeriesTitle = "Dark", SeasonNumber = 2, EpisodeNumber = 1 }, Server);

        Assert.Equal(("Episode 1", null), (info.Title, info.EpisodeTitle));
    }

    [Fact]
    public void AnotherKindOfTitle_HasNothingToFindOnTmdb()
    {
        var info = PlaybackMediaInfo.Of(new WatchDetail { ContentId = "book-1", Type = WatchItemTypes.Audiobook, Title = "Dune", Year = 1965 }, Server);

        Assert.Equal((PlaybackMediaKind.Other, null), (info.Kind, info.CatalogId));
        Assert.Equal(PlaybackMediaKind.Other, PlaybackMediaInfo.None.Kind);
    }

    [Fact]
    public void WithoutAPoster_TheBackdropShows_AndWithNeitherNoPicture()
    {
        var backdrop = PlaybackMediaInfo.Of(new WatchDetail { Type = WatchItemTypes.Movie, Title = "Heat", BackdropUrl = "/b.webp" }, Server);
        var none = PlaybackMediaInfo.Of(new WatchDetail { Type = WatchItemTypes.Movie, Title = "Heat", PosterUrl = "//elsewhere.example/p.webp" }, Server);

        Assert.Equal(new Uri("https://silo.example/b.webp"), backdrop.Artwork);
        Assert.Null(none.Artwork);
        Assert.Equal("", none.Subtitle);
    }
}
