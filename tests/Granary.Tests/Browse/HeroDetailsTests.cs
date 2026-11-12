using System.Globalization;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Requests;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

/// <summary>
/// The line of details under a hero's logo, as the web's <c>formatHeroMetadata</c> (components/heroMetadata.ts): a movie's
/// or a series' rating and genres in it, and where the rating stands, for the hero to draw it as a mark and a score.
/// </summary>
public sealed class HeroDetailsTests
{
    private const string SectionsPath = "api/v2/home/sections";
    private static readonly Uri Server = new("https://silo.example/");

    private static CatalogItem Movie() => new()
    {
        ContentId = "dk",
        Type = CatalogItemTypes.Movie,
        Title = "The Dark Knight",
        Year = 2008,
        Runtime = 152,
        Genres = ["Action", "Crime", "Drama"],
        RatingImdb = 9.04,
        RatingTmdb = 8.5,
        ContentRating = "PG-13",
    };

    private static CatalogItem Series() => new()
    {
        ContentId = "s",
        Type = CatalogItemTypes.Series,
        Title = "Lupin",
        Year = 2006,
        Runtime = 45,
        Genres = ["Drama", "Crime"],
        RatingImdb = 8.4,
        ContentRating = "TV-PG",
    };

    private static BrowseHeroTitle Of(CatalogItem item) => BrowseHeroTitle.From(item, Server, EpisodeSpoilers.Off);

    [Fact]
    public void AMovie_ReadsYearRuntimeRatingTwoGenresAndCertificate()
    {
        Assert.Equal("2008 · 2h 32m · IMDb 9.0 · Action · Crime · PG-13", Of(Movie()).Meta);
    }

    [Fact]
    public void ASeries_ReadsTheSame_WithoutARuntime()
    {
        Assert.Equal("2006 · IMDb 8.4 · Drama · Crime · TV-PG", Of(Series()).Meta);
    }

    [Fact]
    public void AnEpisode_NamesNeitherRatingNorGenres()
    {
        var episode = new CatalogItem
        {
            ContentId = "e1",
            Type = CatalogItemTypes.Episode,
            Title = "Kein Zufall",
            SeriesTitle = "My Name Is Elena",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Runtime = 25,
            Genres = ["Comedy"],
            RatingImdb = 8.1,
            RatingTmdb = 7.9,
            ContentRating = "tv-14",
        };

        var title = Of(episode);

        Assert.Equal("S1 E1 · Kein Zufall · 25m · TV-14", title.Meta);
        Assert.Null(title.Rating);
    }

    [Fact]
    public void WithoutAnImdbScore_TmdbsStandsIn_RoundedAsABadgeRoundsIt()
    {
        var title = Of(Movie() with { RatingImdb = null, RatingTmdb = 7.35 });

        Assert.Equal("2008 · 2h 32m · TMDB 7.4 · Action · Crime · PG-13", title.Meta);
        Assert.Equal(("TMDB", "7.4"), (title.Rating?.Name, title.Rating?.Display));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(10.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AScoreThatIsNoneOutOfTen_IsPassedOver(double imdb)
    {
        Assert.Equal("2008 · 2h 32m · TMDB 8.5 · Action · Crime · PG-13", Of(Movie() with { RatingImdb = imdb }).Meta);

        var unrated = Of(Movie() with { RatingImdb = imdb, RatingTmdb = imdb });
        Assert.Equal("2008 · 2h 32m · Action · Crime · PG-13", unrated.Meta);
        Assert.Null(unrated.Rating);
    }

    [Fact]
    public void AFullTen_IsARating()
    {
        Assert.Equal("2006 · IMDb 10.0 · Drama · Crime · TV-PG", Of(Series() with { RatingImdb = 10 }).Meta);
    }

    [Fact]
    public void TheLine_ReadsTheSame_WhereACommaIsTheDecimalSign()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal("2006 · IMDb 8.4 · Drama · Crime · TV-PG", Of(Series()).Meta);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Genres_AreTrimmed_NamedOnce_AndTwoAtMost()
    {
        var series = Series() with { Genres = [" Drama ", "", "Drama", "  ", "Crime", "Thriller"] };

        Assert.Equal("2006 · IMDb 8.4 · Drama · Crime · TV-PG", Of(series).Meta);
    }

    [Fact]
    public void TheCertificate_IsTrimmed_AndInCapitals()
    {
        Assert.Equal("2006 · IMDb 8.4 · Drama · Crime · NOT RATED", Of(Series() with { ContentRating = " Not Rated " }).Meta);
    }

    [Fact]
    public void WhatATitleLacks_IsLeftOut()
    {
        var series = Series() with { Year = null, Genres = [], ContentRating = null };

        var title = Of(series);

        Assert.Equal("IMDb 8.4", title.Meta);
        Assert.Equal(0, title.Rating?.Start);
        Assert.Equal(("", ""), (title.MetaBeforeRating, title.MetaAfterRating));
    }

    [Fact]
    public void TheRating_IsFoundInTheLine_BetweenWhatComesBeforeAndAfter()
    {
        var title = Of(Movie());

        var rating = Assert.IsType<BrowseHeroRating>(title.Rating);
        Assert.Equal(("IMDb", "9.0", "IMDb 9.0"), (rating.Name, rating.Display, rating.Text));
        Assert.Equal("IMDb 9.0", title.Meta.Substring(rating.Start, rating.Text.Length));
        Assert.Equal("2008 · 2h 32m · ", title.MetaBeforeRating);
        Assert.Equal(" · Action · Crime · PG-13", title.MetaAfterRating);
        Assert.Equal(title.Meta, title.MetaBeforeRating + rating.Text + title.MetaAfterRating);
    }

    [Fact]
    public void ALineWithoutARating_IsAllBeforeIt()
    {
        var title = Of(Movie() with { RatingImdb = null, RatingTmdb = null });

        Assert.Null(title.Rating);
        Assert.Equal("2008 · 2h 32m · Action · Crime · PG-13", title.Meta);
        Assert.Equal((title.Meta, ""), (title.MetaBeforeRating, title.MetaAfterRating));
    }

    [Fact]
    public void ATitleToRequest_KeepsItsLine_WithoutARating()
    {
        var title = BrowseHeroTitle.Of(new RequestMediaResult { TmdbId = 10, MediaType = "movie", Title = "New One", Year = 2026,
            Request = new RequestMediaState { Requestable = true } });

        Assert.Equal("2026 · Movie", title.Meta);
        Assert.Null(title.Rating);
    }

    [Fact]
    public async Task HomesHero_ReadsTheRatingAndGenresOfItsRowsCards()
    {
        var silo = new BrowseFakeSilo();
        silo.Map(SectionsPath, Sections(Section("recent", "recently_added", 1, new
        {
            content_id = "series:lupin",
            type = "series",
            title = "Lupin",
            status = "matched",
            genres = new[] { "Drama", "Crime", "Mystery" },
            keywords = Array.Empty<string>(),
            year = 2006,
            content_rating = "TV-PG",
            rating_imdb = 8.4,
            rating_tmdb = 8.1,
        })));
        var home = new HomeViewModel(BrowseFakeSilo.ReadySession(), silo.Catalog, silo.Images, new RecordingNavigation(), new ImmediateDispatcher(),
            new ManualTimeProvider(), silo.Logs.For<HomeViewModel>(), new ViewerStateChanges(new PlaybackStops()));

        await home.ActivateAsync();

        Assert.Equal("2006 · IMDb 8.4 · Drama · Crime · TV-PG", home.Hero.Shown?.Meta);
        Assert.Equal("IMDb", home.Hero.Shown?.Rating?.Name);
    }
}
