using Granary.Core.Api.Catalog;
using Granary.Core.Texts;
using Granary.Core.ViewModels;

namespace Granary.Tests.Browse;

/// <summary>
/// A Home or library row's title as shown, as the web's <c>lib/sectionTitle.ts</c>: the catalog's message for the
/// row's <c>title_key</c> in a translated interface, filled from <c>title_args</c>, else the server's title; in English
/// the server's title as sent.
/// </summary>
public sealed class SectionTitleTests
{
    /// <summary>
    /// The keyed titles Silo writes today, with its English and values (silo-server 0e95e55b6:
    /// internal/sections/defaults.go, title_keys.go, generated.go, because_watched_title.go,
    /// recommendation_title.go, recipes/text_keys.go and seasonal_catalog.go, internal/recommendations/titles.go).
    /// </summary>
    private static readonly (string Key, string? Name, string? Value, string Title)[] Titles =
    [
        ("home.section.continueWatching", null, null, "Continue Watching"),
        ("home.section.continueListening", null, null, "Continue Listening"),
        ("home.section.continueReading", null, null, "Continue Reading"),
        ("home.section.nextUp", null, null, "Next Up"),
        ("home.section.trendingThisWeek", null, null, "Trending This Week"),
        ("home.section.trendingOnServer", null, null, "Trending on Server"),
        ("home.section.recentlyAdded", null, null, "Recently Added"),
        ("home.section.recentlyAddedMovies", null, null, "Recently Added Movies"),
        ("home.section.recentlyAddedTv", null, null, "Recently Added TV"),
        ("home.section.recentlyAddedAudiobooks", null, null, "Recently Added Audiobooks"),
        ("home.section.recentlyAddedEbooks", null, null, "Recently Added Ebooks"),
        ("home.section.recentlyAddedManga", null, null, "Recently Added Manga"),
        ("home.section.recentlyAddedIn", "library", "Movies", "Recently Added in Movies"),
        ("home.section.recentlyReleased", null, null, "Recently Released"),
        ("home.section.recentlyReleasedMovies", null, null, "Recently Released Movies"),
        ("home.section.recentlyReleasedEpisodes", null, null, "Recently Released Episodes"),
        ("home.section.recentlyReleasedAudiobooks", null, null, "Recently Released Audiobooks"),
        ("home.section.recentlyReleasedEbooks", null, null, "Recently Released Ebooks"),
        ("home.section.recentlyReleasedManga", null, null, "Recently Released Manga"),
        ("home.section.recentlyReleasedIn", "library", "Movies", "Recently Released in Movies"),
        ("home.section.recentlyReleasedEpisodesIn", "library", "TV Shows", "Recently Released Episodes in TV Shows"),
        ("home.section.recommendedForYou", null, null, "Recommended for You"),
        ("home.section.becauseYouWatched", null, null, "Because You Watched"),
        ("home.section.becauseYouWatchedTitle", "title", "Heat", "Because You Watched Heat"),
        ("home.section.returningShows", null, null, "Returning Shows"),
        ("home.section.topRated", null, null, "Top Rated"),
        ("home.section.topRatedMovies", null, null, "Top Rated Movies"),
        ("home.section.topRatedTv", null, null, "Top Rated TV"),
        ("home.section.hiddenGems", null, null, "Hidden Gems"),
        ("home.section.randomPicks", null, null, "Random Picks"),
        ("home.section.nextInYourSeries", null, null, "Next in Your Series"),
        ("home.section.seasonalPicks", null, null, "Seasonal Picks"),
        ("home.section.topPicksToday", null, null, "Top Picks Today"),
        ("home.section.profilesLikeYouEnjoyed", null, null, "Profiles Like You Enjoyed"),
        ("home.section.forYou", null, null, "For You"),
        ("home.section.pickedFromYourHistory", null, null, "Picked from your history"),
        ("home.section.becauseYouEnjoy", "genres", "Crime, Drama", "Because you enjoy Crime, Drama"),
        ("home.section.popularOnServer", null, null, "Popular on This Server"),
        ("home.section.highlyRatedInLibrary", null, null, "Highly Rated in Your Library"),
        ("home.section.topGenre", "genre", "Horror", "Top Horror"),
        ("home.section.seasonal.valentines.romcoms", null, null, "Valentine's Rom-Coms"),
        ("home.section.seasonal.valentines.favorites", null, null, "Valentine's Favorites"),
        ("home.section.seasonal.valentines.romanticDramas", null, null, "Romantic Dramas"),
        ("home.section.seasonal.valentines.loveStories", null, null, "Classic Love Stories"),
        ("home.section.seasonal.stPatricks.irishFilm", null, null, "Irish Film"),
        ("home.section.seasonal.stPatricks.irishTv", null, null, "Irish TV"),
        ("home.section.seasonal.easter.family", null, null, "Easter Movies"),
        ("home.section.seasonal.easter.faith", null, null, "Easter Stories"),
        ("home.section.seasonal.summerBlockbuster.blockbusters", null, null, "Summer Blockbusters"),
        ("home.section.seasonal.summerBlockbuster.classicSummer", null, null, "Classic Summer Movies"),
        ("home.section.seasonal.summerBlockbuster.nostalgia", null, null, "Summer Nostalgia"),
        ("home.section.seasonal.summer.blockbusters", null, null, "Summer Blockbusters"),
        ("home.section.seasonal.summer.classicSummer", null, null, "Classic Summer Movies"),
        ("home.section.seasonal.summer.nostalgia", null, null, "Summer Nostalgia"),
        ("home.section.seasonal.halloween.favorites", null, null, "Halloween Favorites"),
        ("home.section.seasonal.halloween.family", null, null, "Family Halloween"),
        ("home.section.seasonal.halloween.horror", null, null, "Halloween Horror"),
        ("home.section.seasonal.halloween.animated", null, null, "Animated Halloween"),
        ("home.section.seasonal.halloween.tv", null, null, "Halloween TV"),
        ("home.section.seasonal.thanksgiving.favorites", null, null, "Thanksgiving Movies"),
        ("home.section.seasonal.thanksgiving.feelGood", null, null, "Feel-Good Favorites"),
        ("home.section.seasonal.christmas.favorites", null, null, "Christmas Favorites"),
        ("home.section.seasonal.christmas.family", null, null, "Family Christmas"),
        ("home.section.seasonal.christmas.classics", null, null, "Christmas Classics"),
        ("home.section.seasonal.christmas.romcoms", null, null, "Christmas Rom-Coms"),
        ("home.section.seasonal.christmas.comedies", null, null, "Christmas Comedies"),
        ("home.section.seasonal.christmas.hallmark", null, null, "Hallmark Christmas"),
        ("home.section.seasonal.newYear.favorites", null, null, "New Year's Movies"),
        ("home.section.seasonal.saturdayMorning.cartoons", null, null, "Saturday Morning Cartoons"),
        ("home.section.seasonal.saturdayMorning.nineties", null, null, "90s Saturday Mornings"),
        ("home.section.seasonal.saturdayMorning.modern", null, null, "Saturday Morning Favorites"),
        ("home.section.seasonal.familyMovieNight.new", null, null, "Family Movie Night"),
        ("home.section.seasonal.familyMovieNight.favorites", null, null, "Family Favorites"),
        ("home.section.seasonal.familyMovieNight.liveAction", null, null, "Family Movie Night"),
    ];

    public static TheoryData<string, string?, string?, string> ServerTitles
    {
        get
        {
            var data = new TheoryData<string, string?, string?, string>();
            foreach (var (key, name, value, title) in Titles)
            {
                data.Add(key, name, value, title);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ServerTitles))]
    public void InEnglish_ARowShowsTheServersTitle(string key, string? name, string? value, string title)
    {
        Assert.Equal(title, SectionTitle.Shown(Keyed(title, key, name, value)));
    }

    /// <summary>The English catalog words each key as Silo does, so a translation of it is a translation of that title.</summary>
    [Theory]
    [MemberData(nameof(ServerTitles))]
    public void TheEnglishCatalog_WordsEachKeyAsTheServerDoes(string key, string? name, string? value, string title)
    {
        string text = name is null ? AppText.Get(key) : AppText.Get(key, name, value);

        Assert.Equal(title, text);
    }

    [Fact]
    public void TheServersTitles_CoverEveryRowKeyGranaryTakes()
    {
        var listed = File.ReadAllLines(Path.Combine(RepositoryFiles.Root, "src/Granary.Core/Texts/Catalogs/web-keys.txt"))
            .Where(line => line.StartsWith("home.section.", StringComparison.Ordinal));
        var tested = Titles.Select(title => title.Key);

        Assert.Empty(listed.Except(tested, StringComparer.Ordinal));
    }

    [Fact]
    public void InATranslation_AKeyedRowShowsTheCatalogsMessage_FilledFromItsValues()
    {
        using var _ = AppText.Use(TextLanguage.Load("de"));

        string shown = SectionTitle.Shown(Keyed("Recently Added in Filme", "home.section.recentlyAddedIn", "library", "Filme"));

        Assert.Equal(AppText.Get("home.section.recentlyAddedIn", "library", "Filme"), shown);
        Assert.NotEqual("Recently Added in Filme", shown);
        Assert.Contains("Filme", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void InATranslation_ATypedTitle_StaysAsTyped()
    {
        using var _ = AppText.Use(TextLanguage.Load("de"));

        Assert.Equal("Elena's Picks", SectionTitle.Shown(new Section { Title = "Elena's Picks" }));
    }

    [Fact]
    public void InATranslation_AKeyItsCatalogLacks_OrAMissingValue_ShowsTheServersTitle()
    {
        using var _ = AppText.Use(TextLanguage.Load("de"));

        Assert.Equal("Fresh Picks", SectionTitle.Shown(Keyed("Fresh Picks", "home.section.inNoCatalog", null, null)));
        Assert.Equal("Recently Added in Filme", SectionTitle.Shown(Keyed("Recently Added in Filme", "home.section.recentlyAddedIn", null, null)));
    }

    private static Section Keyed(string title, string key, string? name, string? value) => new()
    {
        Title = title,
        TitleKey = key,
        TitleArgs = name is null ? null! : new Dictionary<string, string> { [name] = value! },
    };
}
