using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Detail;

namespace Granary.Tests.Browse;

/// <summary>The detail page's wording, checked against the web client's formatters.</summary>
public sealed class ItemDetailTextTests
{
    private static readonly Uri Server = new("https://silo.test/media");

    private static CatalogItemDetail Item(string type, Action<List<CrewCredit>>? crew = null)
    {
        var credits = new List<CrewCredit>();
        crew?.Invoke(credits);
        return new CatalogItemDetail { ContentId = "item-1", Type = type, Title = "Title", Genres = ["Drama", "Mystery"], Crew = credits };
    }

    [Theory]
    [InlineData(109, "1h 49m")]
    [InlineData(52, "52m")]
    [InlineData(120, "2h 0m")]
    [InlineData(0, null)]
    [InlineData(null, null)]
    public void Runtime_IsHoursAndMinutes(int? minutes, string? expected) => Assert.Equal(expected, DetailText.Runtime(minutes));

    [Theory]
    [InlineData("2021-01-08", "2026-10-23", null, "2021–2026")]
    [InlineData("2021-01-08", "2021-03-01", null, "2021")]
    [InlineData("2021-01-08", null, null, "2021")]
    [InlineData(null, "2026-10-23", 2019, "2019–2026")]
    [InlineData(null, null, null, null)]
    [InlineData("soon", null, 2020, "2020")]
    public void AirYears_SpanTheFirstAndLastAirDates(string? first, string? last, int? year, string? expected) =>
        Assert.Equal(expected, DetailText.AirYears(first, last, year));

    [Theory]
    [InlineData("2026-10-23", "23 Oct 2026")]
    [InlineData("2026-10-23T20:00:00Z", "23 Oct 2026")]
    [InlineData("23.10.2026", null)]
    [InlineData(null, null)]
    public void Date_IsWordedLikeTheWeb(string? iso, string? expected) => Assert.Equal(expected, DetailText.Date(iso));

    [Theory]
    [InlineData(3, 1, "Chapter 1", "S03E01 · Chapter 1")]
    [InlineData(12, 105, " Finale ", "S12E105 · Finale")]
    [InlineData(1, 2, "", "S01E02")]
    public void EpisodeTitle_IsTheCodeAndTitle(int season, int episode, string title, string expected) =>
        Assert.Equal(expected, DetailText.EpisodeTitle(season, episode, title));

    [Theory]
    [InlineData(0, "Staffel 0", false, "Specials")]
    [InlineData(4, "Season 4", true, "Specials")]
    [InlineData(3, "Staffel 3", false, "Staffel 3")]
    [InlineData(2, "", false, "Season 2")]
    public void SeasonDisplayTitle_PrefersTheSeasonsOwnName(int number, string title, bool isSpecials, string expected) =>
        Assert.Equal(expected, DetailText.SeasonDisplayTitle(number, title, isSpecials));

    [Theory]
    [InlineData(CatalogItemTypes.Movie, null, null, null, false, "Play")]
    [InlineData(CatalogItemTypes.Movie, 0.0, true, null, false, "Resume")]
    [InlineData(CatalogItemTypes.Movie, 300.0, null, null, true, "Resume")]
    [InlineData(CatalogItemTypes.Episode, null, false, null, true, "Play")]
    [InlineData(CatalogItemTypes.Series, null, null, 1, false, "Resume")]
    [InlineData(CatalogItemTypes.Series, null, null, 0, false, "Play Next")]
    [InlineData(CatalogItemTypes.Series, null, null, 0, true, "Play")]
    [InlineData(CatalogItemTypes.Season, null, null, 2, false, "Resume")]
    [InlineData(CatalogItemTypes.Season, null, null, 0, true, "Play First Episode")]
    public void PlayAction_ResumesWhatIsInProgress(string type, double? position, bool? isInProgress, int? inProgressCount, bool played, string expected)
    {
        var item = Item(type) with { PlayContentId = type is CatalogItemTypes.Series or CatalogItemTypes.Season ? "next-episode" : null };
        var watch = new WatchRollup
        {
            Played = played,
            PositionSeconds = position,
            IsInProgress = isInProgress,
            InProgressCount = inProgressCount ?? 0,
            WatchedCount = inProgressCount is not null ? 3 : 0,
        };

        Assert.Equal(expected, DetailFacts.PlayAction(item, watch).Label);
    }

    [Fact]
    public void PlayAction_TargetsTheItemOrTheEpisodeTheServerNames()
    {
        Assert.Equal(new DetailPlayAction("Play", "item-1"), DetailFacts.PlayAction(Item(CatalogItemTypes.Movie), null));
        Assert.Equal(new DetailPlayAction("Play", null), DetailFacts.PlayAction(Item(CatalogItemTypes.Series), null));
        var season = Item(CatalogItemTypes.Season) with { PlayContentId = "episode-5" };
        Assert.Equal(new DetailPlayAction("Play First Episode", "episode-1"), DetailFacts.PlayAction(season, null, "episode-1"));
        Assert.Equal(new DetailPlayAction("Play First Episode", "episode-5"), DetailFacts.PlayAction(season, null));
    }

    [Theory]
    [InlineData(CatalogItemTypes.Movie, false, "Mark Watched")]
    [InlineData(CatalogItemTypes.Episode, true, "Mark Unwatched")]
    [InlineData(CatalogItemTypes.Series, false, "Mark Series Watched")]
    [InlineData(CatalogItemTypes.Series, true, "Mark Series Unwatched")]
    [InlineData(CatalogItemTypes.Season, false, "Mark Season Watched")]
    [InlineData(CatalogItemTypes.Season, true, "Mark Season Unwatched")]
    public void WatchedLabel_NamesWhatItMarks(string type, bool isWatched, string expected) =>
        Assert.Equal(expected, DetailFacts.WatchedLabel(type, isWatched));

    [Fact]
    public void Ratings_AreShownAsGivenAndAtMostThree()
    {
        var item = Item(CatalogItemTypes.Movie) with
        {
            Ratings =
            [
                new CatalogRating("imdb", "IMDb", 54, "5.4"),
                new CatalogRating("tmdb", "TMDB", 71, "7.1"),
                new CatalogRating("plugin_x", "", 90, "90/100"),
                new CatalogRating("rt_critic", "Rotten Tomatoes", 98, "98%"),
            ],
        };

        Assert.Equal(["IMDb 5.4", "TMDB 7.1", "PLUGIN_X 90/100"], DetailFacts.Ratings(item).Select(r => r.AccessibleName));
    }

    [Fact]
    public void SeriesWithoutCreators_LeadsWithItsDirectors()
    {
        var series = Item(CatalogItemTypes.Series, crew =>
        {
            crew.Add(new CrewCredit("Ann Lee", "Director", "1", null, null));
            crew.Add(new CrewCredit("Bo Kim", "Director", "2", null, null));
            crew.Add(new CrewCredit("Cy Ng", "Director", "3", null, null));
        });

        Assert.Equal("Directed by Ann Lee, Bo Kim · Drama · Mystery", string.Join(" · ", DetailCredits.Line(series)));
        // Two director cards at most.
        Assert.Equal(["Ann Lee", "Bo Kim"], DetailCredits.People(series, Server).Select(p => p.Name));
    }

    [Fact]
    public void EpisodeCredits_LeaveTheGenresOut_AndSeasonsHaveNone()
    {
        Assert.Empty(DetailCredits.Line(Item(CatalogItemTypes.Episode)));
        Assert.Empty(DetailCredits.Line(Item(CatalogItemTypes.Season, crew => crew.Add(new CrewCredit("Ann Lee", "Director", null, null, null)))));
        Assert.Equal("Drama · Mystery", string.Join(" · ", DetailCredits.Line(Item(CatalogItemTypes.Movie))));
    }

    [Fact]
    public void CreditsWithMissingNames_AreLeftOut()
    {
        var movie = Item(CatalogItemTypes.Movie, crew =>
        {
            crew.Add(new CrewCredit(null!, "Writer", null, null, null));
            crew.Add(new CrewCredit("Ann Lee", "Director", null, null, null));
            crew.Add(new CrewCredit(" ", "Writer", null, null, null));
        }) with
        {
            Cast = [new CastCredit(null!, "Nobody", 0, null, null, null)],
            Ratings = [new CatalogRating(null!, null!, 0, "5.0"), new CatalogRating("imdb", "IMDb", 50, "5.0")],
        };

        Assert.Equal(["Ann Lee"], DetailCredits.People(movie, Server).Select(p => p.Name));
        Assert.Equal("Directed by Ann Lee · Drama · Mystery", string.Join(" · ", DetailCredits.Line(movie)));
        Assert.Equal(["IMDb 5.0"], DetailFacts.Ratings(movie).Select(r => r.AccessibleName));
    }

    [Fact]
    public void OnlyCast_IsTitledCast()
    {
        var item = Item(CatalogItemTypes.Movie) with { Cast = [new CastCredit("Omar Sy", "Assane", 0, null, "/api/v2/artwork/p.webp?sig=x", null)] };

        var people = DetailCredits.People(item, Server);

        Assert.Equal("Cast", DetailCredits.PeopleTitle(people));
        Assert.Equal(new Uri("https://silo.test/media/api/v2/artwork/p.webp?sig=x"), Assert.Single(people).PhotoUrl);
    }

    [Fact]
    public void YouTubeTrailer_OpensTheWatchPage()
    {
        var trailer = DetailTrailer.From(new ItemVideo("Trailer", "YouTube", "xEQP4VVuyrY", true, "Official Trailer", "en"));

        Assert.NotNull(trailer);
        Assert.Equal(new Uri("https://www.youtube.com/watch?v=xEQP4VVuyrY"), trailer.WatchUrl);
        Assert.Equal(new Uri("https://i.ytimg.com/vi/xEQP4VVuyrY/hqdefault.jpg"), trailer.ThumbnailUrl);
        Assert.Equal("Official Trailer, Trailer · Official, opens YouTube", trailer.AccessibleName);
    }

    [Theory]
    [InlineData("Vimeo", "76979871")]
    [InlineData("", "xEQP4VVuyrY")]
    [InlineData("YouTube", "")]
    [InlineData("YouTube", "x&list=evil")]
    [InlineData("YouTube", "../watch")]
    [InlineData("YouTube", "https://evil.example/")]
    public void OtherSitesAndOddKeys_AreSkipped(string site, string key) =>
        Assert.Null(DetailTrailer.From(new ItemVideo("Trailer", site, key, false, null, null)));

    [Fact]
    public void Trailers_KeepTheServerOrderAndEachVideoOnce()
    {
        var trailers = DetailTrailer.From(
        [
            new ItemVideo("teaser", "youtube", "aaaaaaaaaaa", false, null, null),
            new ItemVideo("Clip", "Vimeo", "1", false, "Skipped", null),
            new ItemVideo("behind_the_scenes", "YouTube", "bbbbbbbbbbb", false, "  ", null),
            new ItemVideo("Trailer", "YouTube", "aaaaaaaaaaa", true, "Again", null),
        ]);

        Assert.Equal(["Teaser", "Behind the Scenes"], trailers.Select(t => t.Title));
        Assert.Equal("Teaser", trailers[0].Caption);
    }

    [Theory]
    [InlineData("Featurette", "Featurette")]
    [InlineData("Behind the Scenes", "Behind the Scenes")]
    [InlineData("deleted-scene", "Deleted Scene")]
    [InlineData("interview", "Extra")]
    public void TrailerKinds_AreWordedLikeTheWeb(string kind, string expected) => Assert.Equal(expected, DetailTrailer.KindText(kind));

    [Fact]
    public void VersionLines_NeedSeveralFilesAndNeverShowPaths()
    {
        var one = Item(CatalogItemTypes.Movie) with { Versions = [new FileVersion { FileId = "1", FileName = "secret/path.mkv", Resolution = "1080p" }] };
        var two = one with
        {
            Versions =
            [
                new FileVersion { FileId = "1", FileName = "Heat.mkv", Resolution = "2160p", Hdr = true, CodecVideo = "hevc", CodecAudio = "truehd" },
                new FileVersion { FileId = "2", FileName = "Heat.mp4", Resolution = "720p", CodecVideo = "h264" },
            ],
        };

        Assert.Empty(DetailFacts.VersionLines(one));
        Assert.Equal(["4K HDR · HEVC · TRUEHD", "720P · H264"], DetailFacts.VersionLines(two));
    }

    [Fact]
    public void Overline_UsesTheFirstStudioOrNetwork()
    {
        Assert.Equal("MOVIE", DetailFacts.Overline(Item(CatalogItemTypes.Movie)));
        Assert.Equal("SERIES · HBO", DetailFacts.Overline(Item(CatalogItemTypes.Series) with { Networks = ["", "HBO"] }));
        Assert.Null(DetailFacts.Overline(Item(CatalogItemTypes.Episode)));
        Assert.Equal("PODCAST EPISODE", DetailFacts.Overline(Item(CatalogItemTypes.PodcastEpisode)));
    }

    [Fact]
    public void MovieRuntime_PrefersThePlayedFile()
    {
        var movie = Item(CatalogItemTypes.Movie) with
        {
            Runtime = 120,
            Versions = [new FileVersion { FileId = "1", Duration = 3600 }, new FileVersion { FileId = "2", Duration = 6545 }],
            UserData = new WatchRollup { LastFileId = "2" },
        };

        Assert.Equal(109, DetailFacts.RuntimeMinutes(movie));
        Assert.Equal(120, DetailFacts.RuntimeMinutes(movie with { Versions = [] }));
    }
}
