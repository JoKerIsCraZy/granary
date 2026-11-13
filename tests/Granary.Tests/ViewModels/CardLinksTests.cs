using Granary.Core.Api.Catalog;
using Granary.Core.Api.Requests;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;

namespace Granary.Tests.ViewModels;

/// <summary>
/// The lines under a card as links for the pointer, as on Silo's web cards (<c>hover:underline</c>): which lines, what the
/// title line opens, and that the lines opening the same page go underlined together.
/// </summary>
public sealed class CardLinksTests
{
    private const CardLines TitleAndSubtitle = CardLines.Title | CardLines.Subtitle;

    private static readonly Uri Server = new("https://silo.test/media");

    private static CatalogItem Movie() => new() { ContentId = "movie-1", Type = CatalogItemTypes.Movie, Title = "Masterplan", Year = 2026 };

    private static CatalogItem Episode(string? seriesId = "series-1") => new()
    {
        ContentId = "ep-1", Type = CatalogItemTypes.Episode, Title = "Safe Harbor", SeriesTitle = "Tracker", SeriesId = seriesId,
        SeasonNumber = 4, EpisodeNumber = 1, PositionSeconds = 600, DurationSeconds = 2400,
    };

    private static RequestMediaResult TmdbTitle(long tmdbId = 603) => new()
    {
        TmdbId = tmdbId, MediaType = "movie", Title = "The Matrix", Year = 1999, Request = new RequestMediaState { Requestable = true },
    };

    [Fact]
    public void AMoviesPoster_UnderlinesItsTitleAndItsYearTogether_BothOpenTheMovie()
    {
        var card = MediaCardFactory.Poster(Movie(), Server);

        Assert.Equal(TitleAndSubtitle, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(TitleAndSubtitle, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.Null(card.HeadingId);
        Assert.False(CardLinks.OpensHeading(card, CardLines.Title));
    }

    [Fact]
    public void ASeriesPoster_UnderlinesItsTitleAndItsLineTogether()
    {
        var card = MediaCardFactory.Poster(Movie() with { ContentId = "series-9", Type = CatalogItemTypes.Series }, Server);

        Assert.Equal("2026 SERIES", card.Subtitle);
        Assert.Equal(TitleAndSubtitle, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(TitleAndSubtitle, CardLinks.Underlined(card, CardLines.Subtitle));
    }

    [Fact]
    public void ATitleWithNoLineUnderIt_UnderlinesItsTitleAlone()
    {
        // Only the lines the card shows: a movie without a year has no line under its title.
        var card = MediaCardFactory.Poster(Movie() with { Year = null }, Server);

        Assert.Null(card.Subtitle);
        Assert.Equal(CardLines.Title, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Subtitle));
    }

    [Fact]
    public void AnEpisodesPoster_TitleOpensItsSeries_AndIsUnderlinedAlone()
    {
        // The web's ItemCard and SectionItemCard: headingHref is the series' page.
        var card = MediaCardFactory.Poster(Episode(), Server);

        Assert.Equal("ep-1", card.Id);
        Assert.Equal("series-1", card.HeadingId);
        Assert.True(CardLinks.OpensHeading(card, CardLines.Title));
        Assert.Equal(CardLines.Title, CardLinks.Underlined(card, CardLines.Title));
    }

    [Fact]
    public void AnEpisodesPoster_UnderlinesTheLinesUnderItsTitleTogether_TheyOpenTheEpisode()
    {
        var card = MediaCardFactory.Poster(Episode(), Server);

        var episodeLines = CardLines.EpisodeTitle | CardLines.Subtitle;
        Assert.Equal(episodeLines, CardLinks.Underlined(card, CardLines.EpisodeTitle));
        Assert.Equal(episodeLines, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.False(CardLinks.OpensHeading(card, CardLines.EpisodeTitle));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Subtitle));
        // A row's card is the same card.
        Assert.Equal(card, MediaCardFactory.ForSection("recently_added", Episode(), Server));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEpisodeWhoseSeriesIsNotKnown_OpensTheEpisodeByEveryLine_AllUnderlinedTogether(string? seriesId)
    {
        var card = MediaCardFactory.Poster(Episode(seriesId), Server);

        var all = CardLines.Title | CardLines.EpisodeTitle | CardLines.Subtitle;
        Assert.Null(card.HeadingId);
        Assert.Equal(all, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(all, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Title));
    }

    [Theory]
    [InlineData(SectionTypes.ContinueWatching)]
    [InlineData(SectionTypes.NextUp)]
    public void AContinueWatchingEpisode_TitleOpensTheSeries_TheLinesUnderItTheEpisode(string sectionType)
    {
        // The web's ContinueWatchingCard: the heading links to headingHref, the two lines under it to the card's own item.
        var card = MediaCardFactory.ForSection(sectionType, Episode(), Server);

        var episodeLines = CardLines.Subtitle | CardLines.Caption;
        Assert.Equal(MediaCardShape.Landscape, card.Shape);
        Assert.Equal("series-1", card.HeadingId);
        Assert.True(CardLinks.OpensHeading(card, CardLines.Title));
        Assert.Equal(CardLines.Title, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(episodeLines, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.Equal(episodeLines, CardLinks.Underlined(card, CardLines.Caption));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Subtitle));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Caption));
    }

    [Fact]
    public void ANextUpCardThatOnlySaysNewSeason_HasNoCaptionLineToUnderline()
    {
        // The pill stands where "Next Episode" would: it is no link.
        var premiere = Episode() with { ItemSource = ItemSources.NextUp, Badges = ["season_premiere"] };
        var card = MediaCardFactory.Landscape(premiere, Server);

        Assert.Null(card.Caption);
        Assert.Equal("NEW SEASON", card.CaptionBadge);
        Assert.Equal(CardLines.Subtitle, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Caption));
    }

    [Fact]
    public void AContinueWatchingMovie_UnderlinesEveryLineTogether()
    {
        var card = MediaCardFactory.Landscape(Movie() with { PositionSeconds = 600, DurationSeconds = 6000 }, Server);

        var all = CardLines.Title | CardLines.Subtitle | CardLines.Caption;
        Assert.Null(card.HeadingId);
        Assert.Equal(all, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(all, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.Equal(all, CardLinks.Underlined(card, CardLines.Caption));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AContinueWatchingEpisodeWhoseSeriesIdIsBlank_TitleOpensTheEpisode(string? seriesId)
    {
        var card = MediaCardFactory.Landscape(Episode(seriesId), Server);

        Assert.Equal("Tracker", card.Title);
        Assert.Null(card.HeadingId);
        Assert.False(CardLinks.OpensHeading(card, CardLines.Title));
        Assert.Equal(CardLines.Title | CardLines.Subtitle | CardLines.Caption, CardLinks.Underlined(card, CardLines.Title));
    }

    [Fact]
    public void AContinueWatchingEpisodeWithoutASeriesTitle_TitleOpensTheEpisode()
    {
        // Its heading is the episode's own title, as the web's card, so it stands for the episode.
        var card = MediaCardFactory.Landscape(Episode() with { SeriesTitle = null }, Server);

        Assert.Equal("Safe Harbor", card.Title);
        Assert.Null(card.HeadingId);
        Assert.False(CardLinks.OpensHeading(card, CardLines.Title));
        // An empty series title is none either.
        Assert.Null(MediaCardFactory.Landscape(Episode() with { SeriesTitle = "" }, Server).HeadingId);
    }

    [Fact]
    public void ABoxSetsCard_UnderlinesItsNameAndItsLineTogether_BothOpenTheBoxSet()
    {
        var card = MediaCardFactory.BoxSet(new CatalogBoxSet { Id = "10", LibraryId = "3", Title = "Masterplan Box Set", MovieCount = 3 }, Server);

        Assert.Equal(TitleAndSubtitle, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(TitleAndSubtitle, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Title));
    }

    [Fact]
    public void ATitleFromTmdb_UnderlinesItsTitleAndItsYearTogether()
    {
        var card = RequestCards.Of(TmdbTitle());

        Assert.Equal(TitleAndSubtitle, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(TitleAndSubtitle, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Title));
    }

    [Fact]
    public void ATitleFromTmdbWithNoPageToOpen_HasNoLinks()
    {
        // Neither in the library nor known to TMDB by an id: a click on its card opens nothing.
        var result = TmdbTitle(tmdbId: 0);
        Assert.Null(RequestCards.Destination(result));

        var card = RequestCards.Of(result);

        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Subtitle));
    }

    [Fact]
    public void AWatchlistTitle_UnderlinesItsTitleAndItsLineTogether_ButNeverItsStatus()
    {
        // The web's WatchlistTitleCard: the status under the two links is plain text.
        var title = new WatchlistTitle { TmdbId = 603, MediaType = "movie", Title = "The Matrix", Year = 1999 };
        var card = RequestCards.Of(title, WatchlistTitleStatus.Of(title, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc));

        Assert.Equal("Not requested", card.Caption);
        Assert.Equal(TitleAndSubtitle, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(TitleAndSubtitle, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Caption));
    }

    [Fact]
    public void BetweenTwoLinesOfOneLink_BothStayUnderlined()
    {
        // The gap between a movie's title and its year, and between an episode's own title and its code.
        var movie = MediaCardFactory.Poster(Movie(), Server);
        var episode = MediaCardFactory.Poster(Episode(), Server);

        Assert.Equal(TitleAndSubtitle, CardLinks.Underlined(movie, CardLines.Title, CardLines.Subtitle));
        Assert.Equal(CardLines.EpisodeTitle | CardLines.Subtitle, CardLinks.Underlined(episode, CardLines.EpisodeTitle, CardLines.Subtitle));
    }

    [Fact]
    public void BetweenTwoLinesThatOpenDifferentPages_OrALinkAndPlainText_NothingIsUnderlined()
    {
        var episode = MediaCardFactory.Poster(Episode(), Server);
        var title = new WatchlistTitle { TmdbId = 603, MediaType = "movie", Title = "The Matrix", Year = 1999 };
        var watchlist = RequestCards.Of(title, WatchlistTitleStatus.Of(title, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc));

        // The series' name above, the episode's lines below.
        Assert.Equal(CardLines.None, CardLinks.Underlined(episode, CardLines.Title, CardLines.EpisodeTitle));
        // The line under the title above, the plain status below.
        Assert.Equal(CardLines.None, CardLinks.Underlined(watchlist, CardLines.Subtitle, CardLines.Caption));
        // Above the first line and under the last.
        Assert.Equal(CardLines.None, CardLinks.Underlined(episode, CardLines.None, CardLines.Title));
    }

    [Fact]
    public void SeasonAndEpisodeCardsOfATitlesPage_HaveNoLinks_AsTheWebsCarousels()
    {
        var season = MediaCardFactory.Season(new Season { ContentId = "season-1", Title = "Season 1", EpisodeCount = 8 }, Server);
        var episode = MediaCardFactory.Episode(new Episode { ContentId = "ep-1", Title = "Pilot", SeasonNumber = 1, EpisodeNumber = 1, Runtime = 52 }, Server);

        foreach (var line in new[] { CardLines.Title, CardLines.Subtitle, CardLines.Caption })
        {
            Assert.Equal(CardLines.None, CardLinks.Underlined(season, line));
            Assert.Equal(CardLines.None, CardLinks.Underlined(episode, line));
        }
    }

    [Fact]
    public void ACardMadeWithoutLinks_HasNone()
    {
        // Collections' and the calendar's cards: the web's link them whole and underline nothing.
        var card = new MediaCard("collection-1", "collection", "Heist Night") { Subtitle = "4 TITLES" };

        Assert.Equal(CardLines.None, card.Links);
        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(CardLines.None, CardLinks.Underlined(MediaCard.Placeholder, CardLines.Title));
        Assert.Equal(CardLines.None, CardLinks.Underlined(null, CardLines.Title));
    }

    [Fact]
    public void WhileItsGridSelects_ACardHasNoLinks()
    {
        // History's Select: a click anywhere on the card picks it.
        var card = MediaCardFactory.Poster(Episode(), Server);

        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Title, isSelecting: true));
        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Subtitle, isSelecting: true));
        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.EpisodeTitle, CardLines.Subtitle, isSelecting: true));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Title, isSelecting: true));
    }

    [Fact]
    public void TheFilmsOwnCardInItsBoxSet_HasNoLinks_ItOpensNothing()
    {
        var card = MediaCardFactory.Poster(Movie(), Server) with { CurrentMark = CurrentMark.ThisFilm };

        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Subtitle));
    }

    [Fact]
    public void NoLineUnderThePointer_UnderlinesNothing()
    {
        var card = MediaCardFactory.Poster(Episode(), Server);

        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.None));
        Assert.False(CardLinks.OpensHeading(card, CardLines.None));
    }

    [Theory]
    [InlineData(CardPointer.Artwork, true)]
    [InlineData(CardPointer.Caption, false)]
    [InlineData(CardPointer.Away, false)]
    public void TheArtworksHoverLook_ShowsOnlyWhileThePointerIsOnTheArtwork(CardPointer pointer, bool shows)
    {
        // The web's overlay belongs to the artwork's box (group/media): over the caption only the link's underline shows.
        var card = MediaCardFactory.Poster(Movie(), Server);

        Assert.Equal(shows, CardHover.ShowsArtworkLook(card, pointer));
        // A card without links is no different: a season's caption brings no play disc either.
        Assert.Equal(shows, CardHover.ShowsArtworkLook(new MediaCard("season-1", CatalogItemTypes.Season, "Season 1"), pointer));
    }

    [Theory]
    [InlineData(CardPointer.Artwork)]
    [InlineData(CardPointer.Caption)]
    public void WithoutACardOrWhileItsGridSelects_NoHoverLookShows(CardPointer pointer)
    {
        var card = MediaCardFactory.Poster(Movie(), Server);

        Assert.False(CardHover.ShowsArtworkLook(null, pointer));
        Assert.False(CardHover.ShowsArtworkLook(card, pointer, isSelecting: true));
    }

    [Fact]
    public void OnlyThePointerOnTheCaption_UnderlinesALine()
    {
        var card = MediaCardFactory.Poster(Movie(), Server);

        Assert.Equal(TitleAndSubtitle, CardHover.Underlined(card, CardPointer.Caption, CardLines.Title));
        Assert.Equal(TitleAndSubtitle, CardHover.Underlined(card, CardPointer.Caption, CardLines.Title, CardLines.Subtitle));
        Assert.Equal(CardLines.None, CardHover.Underlined(card, CardPointer.Artwork, CardLines.Title));
        Assert.Equal(CardLines.None, CardHover.Underlined(card, CardPointer.Away, CardLines.Title));
        Assert.Equal(CardLines.None, CardHover.Underlined(card, CardPointer.Caption, CardLines.Title, isSelecting: true));
    }
}
