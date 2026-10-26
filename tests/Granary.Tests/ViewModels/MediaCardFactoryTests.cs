using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Cards;

namespace Granary.Tests.ViewModels;

public sealed class MediaCardFactoryTests
{
    private static readonly Uri Server = new("https://silo.test/media");

    private static CatalogItem Movie() => new()
    {
        ContentId = "movie-1",
        Type = CatalogItemTypes.Movie,
        Title = "Masterplan",
        Year = 2026,
        PosterUrl = "/api/v2/artwork/p.webp?sig=a",
        BackdropUrl = "/api/v2/artwork/b.webp?sig=b",
        OverlaySummary = new CatalogItemOverlay { Resolution = "1080p", Audio = "eac3" },
        RatingImdb = 5.44,
        UserState = new CatalogItemUserState(false, false, false, false),
    };

    [Fact]
    public void MoviePoster_ShowsYearBadgesAndRating()
    {
        var card = MediaCardFactory.Poster(Movie(), Server);

        Assert.Equal(MediaCardShape.Poster, card.Shape);
        Assert.Equal("Masterplan", card.Title);
        Assert.Equal("2026", card.Subtitle);
        Assert.Equal("1080P", card.ResolutionBadge);
        Assert.Equal("EAC3", card.AudioBadge);
        Assert.Equal("IMDB 5.4", card.RatingBadge);
        Assert.Null(card.StatusBadge);
        Assert.Equal(new Uri("https://silo.test/media/api/v2/artwork/p.webp?sig=a"), card.ImageUrl);
        Assert.Null(card.Progress);
    }

    [Theory]
    [InlineData("2160p", "DV.HDR10", "4K DV")]
    [InlineData("4k", null, "4K")]
    [InlineData("1080p", "HDR10", "1080P HDR")]
    [InlineData("4320p", null, "8K")]
    [InlineData("720p", "HLG", "720P HDR")]
    [InlineData(null, "DV", null)]
    public void Resolution_IsWordedLikeTheWeb(string? resolution, string? hdr, string? expected) =>
        Assert.Equal(expected, MediaCardFactory.ResolutionText(new CatalogItemOverlay { Resolution = resolution, Hdr = hdr }));

    [Theory]
    [InlineData("Returning Series", "RETURNING")]
    [InlineData("continuing", "RETURNING")]
    [InlineData("Ended", "ENDED")]
    [InlineData("canceled", "CANCELLED")]
    [InlineData("planned", "UPCOMING")]
    [InlineData("pilot", "PILOT")]
    [InlineData("", null)]
    public void ShowStatus_IsWordedLikeTheWeb(string status, string? expected) =>
        Assert.Equal(expected, MediaCardFactory.ShowStatusText(status));

    [Fact]
    public void EpisodePoster_ShowsSeriesEpisodeTitleAndCode_AsTheWebGrid()
    {
        var episode = new CatalogItem
        {
            ContentId = "ep-1", Type = CatalogItemTypes.Episode, Title = "Safe Harbor", SeriesTitle = "Tracker",
            SeasonNumber = 4, EpisodeNumber = 1, Year = 2026,
        };

        var card = MediaCardFactory.Poster(episode, Server);

        Assert.Equal("Tracker", card.Title);
        Assert.Equal("Safe Harbor", card.EpisodeTitle);
        Assert.Equal("S04E01", card.Subtitle);
        Assert.Equal("Tracker, Safe Harbor, S04E01", card.AccessibleName);
    }

    [Fact]
    public void EpisodePoster_WithoutASeriesTitleOrNumbers_KeepsOneTitle()
    {
        var untitled = new CatalogItem { ContentId = "ep-1", Type = CatalogItemTypes.Episode, Title = "Pilot", SeasonNumber = 1, EpisodeNumber = 12 };
        var unnumbered = new CatalogItem { ContentId = "ep-2", Type = CatalogItemTypes.Episode, Title = "Special", SeriesTitle = "Tracker", Year = 2024 };

        var first = MediaCardFactory.Poster(untitled, Server);
        var second = MediaCardFactory.Poster(unnumbered, Server);

        Assert.Equal(("Pilot", null, "S01E12"), (first.Title, first.EpisodeTitle, first.Subtitle));
        Assert.Equal(("Tracker", null, "2024"), (second.Title, second.EpisodeTitle, second.Subtitle));
    }

    [Fact]
    public void SeriesPoster_ShowsYearSeriesAndStatus()
    {
        var series = new CatalogItem { ContentId = "s-1", Type = CatalogItemTypes.Series, Title = "Lupin", Year = 2021, ShowStatus = "Returning Series" };

        var card = MediaCardFactory.Poster(series, Server);

        Assert.Equal("2021 SERIES", card.Subtitle);
        Assert.Equal("RETURNING", card.StatusBadge);
        Assert.Null(card.ImageUrl);
    }

    [Fact]
    public void ContinueWatchingEpisode_IsALandscapeCardWithTimeLeft()
    {
        var episode = new CatalogItem
        {
            ContentId = "ep-1",
            Type = CatalogItemTypes.Episode,
            Title = "Schulanfang",
            SeriesTitle = "ted",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            PositionSeconds = 600,
            DurationSeconds = 1680,
            BackdropUrl = "https://cdn.test/still.webp",
            ItemSource = ItemSources.InProgress,
        };
        var section = new Section { Id = "cw", SectionType = SectionTypes.ContinueWatching, Title = "Continue Watching" };

        var card = MediaCardFactory.ForSection(section, episode, Server);

        Assert.Equal(MediaCardShape.Landscape, card.Shape);
        Assert.Equal("ted", card.Title);
        Assert.Equal("Season 1 Episode 1 • Schulanfang", card.Subtitle);
        Assert.Equal("18 min left", card.Caption);
        Assert.Equal(600.0 / 1680, card.Progress!.Value, 3);
        Assert.Equal(new Uri("https://cdn.test/still.webp"), card.ImageUrl);
    }

    [Fact]
    public void NextUpCard_SaysNextEpisode()
    {
        var next = new CatalogItem
        {
            ContentId = "ep-2", Type = CatalogItemTypes.Episode, Title = "Blind Rooster", SeriesTitle = "Tulsa King",
            SeasonNumber = 4, EpisodeNumber = 2, ItemSource = ItemSources.NextUp,
        };

        var card = MediaCardFactory.Landscape(next, Server);

        Assert.Equal("Next episode", card.Caption);
        Assert.Null(card.Progress);
    }

    [Fact]
    public void RecentlyAddedRow_UsesPosters()
    {
        var section = new Section { Id = "ra", SectionType = SectionTypes.RecentlyAdded, Title = "Recently Added Movies" };

        Assert.Equal(MediaCardShape.Poster, MediaCardFactory.ForSection(section, Movie(), Server).Shape);
    }

    [Fact]
    public void WatchedTitle_HasNeitherProgressNorAMark_ButKnowsItsState()
    {
        // The web shows it on hover only (the watched button turns green).
        var movie = Movie() with { PositionSeconds = 100, DurationSeconds = 200, UserState = new CatalogItemUserState(true, true, true, false) };

        var card = MediaCardFactory.Poster(movie, Server);

        Assert.False(card.ShowsWatchedMark);
        Assert.Null(card.Progress);
        Assert.True(card.IsWatched);
        Assert.True(card.IsFavorite);
        Assert.True(card.InWatchlist);
    }

    [Fact]
    public void Season_ShowsHowManyEpisodesAreWatched()
    {
        var season = new Season
        {
            ContentId = "season-3", SeasonNumber = 3, Title = "Staffel 3", EpisodeCount = 8,
            UserData = new WatchRollup { WatchedCount = 4 }, PosterUrl = "/api/v2/artwork/s3.webp",
        };

        var card = MediaCardFactory.Season(season, Server);

        Assert.Equal("Staffel 3", card.Title);
        Assert.Equal("4 of 8 episodes", card.Caption);
        Assert.Equal(0.5, card.Progress);
        Assert.False(card.IsWatched);
    }

    [Fact]
    public void Episode_ShowsCodeTitleDateAndRuntime()
    {
        var episode = new Episode
        {
            ContentId = "ep-5", SeasonNumber = 3, EpisodeNumber = 1, Title = "Chapter 1", Runtime = 52, AirDate = "2026-10-23",
            Overview = "Assane devises a plan.", StillUrl = "/api/v2/artwork/e.webp",
            UserData = new WatchRollup { Played = true },
        };

        var card = MediaCardFactory.Episode(episode, Server);

        Assert.Equal("S03E01 · Chapter 1", card.Title);
        Assert.Equal("23 Oct 2026 · 52m", card.Subtitle);
        Assert.Equal("Assane devises a plan.", card.Caption);
        Assert.True(card.IsWatched);
        Assert.True(card.ShowsWatchedMark);
        Assert.Equal("ep-5", card.PlayContentId);
    }
}
