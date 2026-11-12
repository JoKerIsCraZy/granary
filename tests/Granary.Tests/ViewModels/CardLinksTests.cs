using Granary.Core.Api.Catalog;
using Granary.Core.Api.Requests;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;

namespace Granary.Tests.ViewModels;

/// <summary>
/// The lines under a card as links of their own for the pointer, as on Silo's web cards (<c>hover:underline</c>): which
/// lines, what goes underlined together, and what the title line opens.
/// </summary>
public sealed class CardLinksTests
{
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
    public void AMoviesPoster_LinksItsTitleAndItsYear_BothToItself()
    {
        var card = MediaCardFactory.Poster(Movie(), Server);

        Assert.Equal(CardLines.Title, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(CardLines.Subtitle, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.Null(card.HeadingId);
        Assert.False(CardLinks.OpensHeading(card, CardLines.Title));
    }

    [Fact]
    public void AnEpisodesPoster_TitleOpensItsSeries_TheLinesUnderItTheEpisode()
    {
        // The web's ItemCard: headingHref is the series' page, the episode's title and its code link to itemHref.
        var card = MediaCardFactory.Poster(Episode(), Server);

        Assert.Equal("ep-1", card.Id);
        Assert.Equal("series-1", card.HeadingId);
        Assert.True(CardLinks.OpensHeading(card, CardLines.Title));
        Assert.False(CardLinks.OpensHeading(card, CardLines.EpisodeTitle));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Subtitle));
        // A grid's card underlines each line by itself.
        Assert.Equal(CardLines.EpisodeTitle, CardLinks.Underlined(card, CardLines.EpisodeTitle));
        Assert.Equal(CardLines.Subtitle, CardLinks.Underlined(card, CardLines.Subtitle));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEpisodeWhoseSeriesIsNotKnown_TitleOpensTheEpisode(string? seriesId)
    {
        var card = MediaCardFactory.Poster(Episode(seriesId), Server);

        Assert.Null(card.HeadingId);
        Assert.Equal(CardLines.Title, CardLinks.Underlined(card, CardLines.Title));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Title));
    }

    [Fact]
    public void ARowsEpisodePoster_UnderlinesTheEpisodesTitleAndCodeTogether_AsTheWebsOneLink()
    {
        // The web's SectionItemCard wraps both lines in one link: the pointer over either underlines both.
        var card = MediaCardFactory.ForSection("recently_added", Episode(), Server);

        var both = CardLines.EpisodeTitle | CardLines.Subtitle;
        Assert.Equal(MediaCardShape.Poster, card.Shape);
        Assert.Equal(both, CardLinks.Underlined(card, CardLines.EpisodeTitle));
        Assert.Equal(both, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.Equal(CardLines.Title, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal("series-1", card.HeadingId);
        // A row's movie has one line under its title.
        var movie = MediaCardFactory.ForSection("recently_added", Movie(), Server);
        Assert.Equal(CardLines.Subtitle, CardLinks.Underlined(movie, CardLines.Subtitle));
    }

    [Theory]
    [InlineData(SectionTypes.ContinueWatching)]
    [InlineData(SectionTypes.NextUp)]
    public void AContinueWatchingCard_TitleOpensTheSeries_EpisodeLineAndTimeLeftTheEpisode(string sectionType)
    {
        // The web's ContinueWatchingCard: the heading links to headingHref, the two lines under it to the card's own item.
        var card = MediaCardFactory.ForSection(sectionType, Episode(), Server);

        Assert.Equal(MediaCardShape.Landscape, card.Shape);
        Assert.Equal("series-1", card.HeadingId);
        Assert.True(CardLinks.OpensHeading(card, CardLines.Title));
        Assert.Equal(CardLines.Subtitle, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.Equal(CardLines.Caption, CardLinks.Underlined(card, CardLines.Caption));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Subtitle));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Caption));
    }

    [Fact]
    public void AContinueWatchingMovie_LinksEveryLineToItself()
    {
        var card = MediaCardFactory.Landscape(Movie() with { PositionSeconds = 600, DurationSeconds = 6000 }, Server);

        Assert.Null(card.HeadingId);
        Assert.Equal(CardLines.Title, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(CardLines.Caption, CardLinks.Underlined(card, CardLines.Caption));
    }

    [Fact]
    public void ABoxSetsCard_LinksItsNameAndItsLine_ToTheBoxSet()
    {
        var card = MediaCardFactory.BoxSet(new CatalogBoxSet { Id = "10", LibraryId = "3", Title = "Masterplan Box Set", MovieCount = 3 }, Server);

        Assert.Equal(CardLines.Title, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(CardLines.Subtitle, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.False(CardLinks.OpensHeading(card, CardLines.Title));
    }

    [Fact]
    public void ATitleFromTmdb_LinksItsTitleAndItsYear_ToItsPage()
    {
        var card = RequestCards.Of(TmdbTitle());

        Assert.Equal(CardLines.Title, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(CardLines.Subtitle, CardLinks.Underlined(card, CardLines.Subtitle));
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
    public void AWatchlistTitle_LinksItsTitleAndItsLine_ButNotItsStatus()
    {
        // The web's WatchlistTitleCard: the status under the two links is plain text.
        var title = new WatchlistTitle { TmdbId = 603, MediaType = "movie", Title = "The Matrix", Year = 1999 };
        var card = RequestCards.Of(title, WatchlistTitleStatus.Of(title, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc));

        Assert.Equal("Not requested", card.Caption);
        Assert.Equal(CardLines.Title, CardLinks.Underlined(card, CardLines.Title));
        Assert.Equal(CardLines.Subtitle, CardLinks.Underlined(card, CardLines.Subtitle));
        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.Caption));
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
        var card = MediaCardFactory.ForSection("recently_added", Episode(), Server);

        Assert.Equal(CardLines.None, CardLinks.Underlined(card, CardLines.None));
        Assert.False(CardLinks.OpensHeading(card, CardLines.None));
    }
}
