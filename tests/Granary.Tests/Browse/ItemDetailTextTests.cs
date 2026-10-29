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
    [InlineData(CatalogItemTypes.Series, null, null, 0, true, "Start From Episode 1")]
    [InlineData(CatalogItemTypes.Series, null, null, null, false, "Start From Episode 1")]
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
        // Nothing to play in a series: the web's "Browse Series", with no target.
        Assert.Equal(new DetailPlayAction("Browse Series", null), DetailFacts.PlayAction(Item(CatalogItemTypes.Series), null));
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

        // Two director cards at most.
        Assert.Equal(["Ann Lee", "Bo Kim"], DetailCredits.People(series, Server).Select(p => p.Name));
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
        Assert.Equal([("Director", "Ann Lee")], DetailFacts.InfoSections(movie).Single(s => s.Title == "Crew").Rows.Select(r => (r.Label, r.Value)));
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
    public void Files_NeverShowTheirNamesOrPaths()
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

        Assert.Equal("Available version: 1080P", DetailFacts.AvailableVersionLine(one));
        Assert.Equal("Available versions: 4K HDR · 720P", DetailFacts.AvailableVersionLine(two));
        Assert.DoesNotContain(DetailFacts.InfoSections(one).Concat(DetailFacts.InfoSections(two)).SelectMany(s => s.Rows),
            r => r.Value.Contains("secret", StringComparison.Ordinal) || r.Value.Contains("Heat.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1_073_741_823L, "1.0 GB")]
    [InlineData(1_048_575L, "1 MB")]
    [InlineData(2_048L, "2 KB")]
    [InlineData(500L, "1 KB")]
    public void FileSizes_PickTheirUnitAfterRounding(long bytes, string expected) =>
        Assert.Equal("Available version: " + expected, DetailFacts.AvailableVersionLine(Item(CatalogItemTypes.Movie) with
        {
            Versions = [new FileVersion { FileId = "1", FileSize = bytes }],
        }));

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

    [Fact]
    public void MetaLine_OfAMovie_IsYearRuntimeRatingAndGenres()
    {
        var movie = Item(CatalogItemTypes.Movie) with { Year = 2026, Runtime = 118, ContentRating = "R", Genres = ["Mystery", "Thriller"] };

        Assert.Equal("2026 · 1h 58m · R · Mystery, Thriller", DetailFacts.MetaLine(movie));
    }

    [Fact]
    public void MetaLine_OfASeries_CountsItsRegularSeasons_AndKeepsThreeGenres()
    {
        var series = Item(CatalogItemTypes.Series) with
        {
            FirstAirDate = "2021-01-08",
            LastAirDate = "2026-10-23",
            ContentRating = "TV-MA",
            SeasonCount = 3,
            Genres = ["Crime", "Drama", "Mystery", "Thriller"],
        };
        Season[] seasons = [new() { SeasonNumber = 0, IsSpecials = true }, new() { SeasonNumber = 1 }, new() { SeasonNumber = 2 }];

        Assert.Equal("2021–2026 · TV-MA · 3 seasons · Crime, Drama, Mystery", DetailFacts.MetaLine(series));
        Assert.Equal("2021–2026 · TV-MA · 2 seasons · Crime, Drama, Mystery", DetailFacts.MetaLine(series, seasons));
    }

    [Fact]
    public void MetaLine_OfAnEpisode_IsItsCodeRuntimeAirDateAndRating()
    {
        var episode = Item(CatalogItemTypes.Episode) with { SeasonNumber = 3, EpisodeNumber = 1, Runtime = 47, AirDate = "2021-01-08", ContentRating = "TV-MA" };

        Assert.Equal("S3 E1 · 47m · 8 Jan 2021 · TV-MA", DetailFacts.MetaLine(episode));
    }

    [Fact]
    public void MetaLine_OfASeason_IsItsYearAndEpisodes_AndLeavesOutWhatIsUnknown()
    {
        Assert.Equal("2021 · 10 episodes", DetailFacts.MetaLine(Item(CatalogItemTypes.Season) with { AirDate = "2021-01-08", EpisodeCount = 10, Genres = [] }));
        Assert.Equal("", DetailFacts.MetaLine(Item(CatalogItemTypes.Movie) with { Genres = [] }));
    }

    [Fact]
    public void AvailableVersion_NamesTheOneFile_WithoutItsPath()
    {
        var movie = Item(CatalogItemTypes.Movie) with
        {
            Versions = [new FileVersion { FileId = "1", FileName = "secret/path.mkv", Resolution = "720p", CodecVideo = "h264", CodecAudio = "eac3", FileSize = 9_985_769_062 }],
        };

        Assert.Equal("Available version: 720P · H264 · EAC3 · 9.3 GB", DetailFacts.AvailableVersionLine(movie));
        Assert.Equal("Available version: 720P · 700 MB", DetailFacts.AvailableVersionLine(movie with
        {
            Versions = [new FileVersion { FileId = "1", Resolution = "720p", FileSize = 734_003_200 }],
        }));
    }

    [Fact]
    public void AvailableVersions_ListTheirQualities_AndASeriesHasNone()
    {
        var movie = Item(CatalogItemTypes.Movie) with
        {
            Versions =
            [
                new FileVersion { FileId = "1", Resolution = "2160p", Hdr = true },
                new FileVersion { FileId = "2", Resolution = "1080p" },
                new FileVersion { FileId = "3", Resolution = "1080p" },
            ],
        };

        Assert.Equal("Available versions: 4K HDR · 1080P", DetailFacts.AvailableVersionLine(movie));
        Assert.Null(DetailFacts.AvailableVersionLine(Item(CatalogItemTypes.Series)));
    }

    [Theory]
    [InlineData(3753.4, "Resume from 1:02:33")]
    [InlineData(725.0, "Resume from 12:05")]
    [InlineData(null, "Play")]
    public void PlayButton_SaysWhereAMovieResumes(double? position, string expected)
    {
        var movie = Item(CatalogItemTypes.Movie);
        var watch = new WatchRollup { PositionSeconds = position, IsInProgress = position is not null };

        Assert.Equal(expected, DetailFacts.PlayButtonText(movie, watch, DetailFacts.PlayAction(movie, watch)));
    }

    [Theory]
    [InlineData(125.0, true, "Resume from 2:05")]
    [InlineData(0.4, true, "Resume")]
    [InlineData(360_061.0, true, "Resume from 100:01:01")]
    public void PlayButton_OfAnEpisode_SaysWhereItResumes_FromASecondOn(double position, bool inProgress, string expected)
    {
        var episode = Item(CatalogItemTypes.Episode);
        var watch = new WatchRollup { PositionSeconds = position, IsInProgress = inProgress };

        Assert.Equal(expected, DetailFacts.PlayButtonText(episode, watch, DetailFacts.PlayAction(episode, watch)));
    }

    [Fact]
    public void PlayButton_OfASeries_KeepsItsAction()
    {
        var series = Item(CatalogItemTypes.Series) with { PlayContentId = "next" };
        var watch = new WatchRollup { InProgressCount = 1, PositionSeconds = 600 };

        Assert.Equal("Resume", DetailFacts.PlayButtonText(series, watch, DetailFacts.PlayAction(series, watch)));
    }

    [Fact]
    public void Info_HasTheDetailsCastCrewAndFile_WithoutPaths()
    {
        var movie = Item(CatalogItemTypes.Movie, crew => crew.Add(new CrewCredit("Ann Lee", "Director", null, null, null))) with
        {
            Title = "Elena",
            Year = 2026,
            Runtime = 118,
            ContentRating = "R",
            Overview = "A long story.",
            Countries = ["France"],
            Ratings = [new CatalogRating("imdb", "IMDb", 70, "7.0")],
            Cast = [new CastCredit("Maya Stone", "Elena", 0, null, null, null)],
            Versions = [new FileVersion { FileId = "1", FileName = "secret/path.mkv", Resolution = "720p", CodecVideo = "h264", CodecAudio = "eac3", Container = "mkv", FileSize = 9_985_769_062, Bitrate = 8000 }],
        };

        var sections = DetailFacts.InfoSections(movie);

        Assert.Equal(["Details", "Cast", "Crew", "File"], sections.Select(s => s.Title));
        var details = sections[0].Rows.ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("1h 58m", details["Runtime"]);
        Assert.Equal("Drama, Mystery", details["Genres"]);
        Assert.Equal("IMDb 7.0", details["Ratings"]);
        Assert.Equal("A long story.", details["Overview"]);
        Assert.Equal(("Maya Stone", "Elena"), (sections[1].Rows[0].Label, sections[1].Rows[0].Value));
        Assert.Equal(("Director", "Ann Lee"), (sections[2].Rows[0].Label, sections[2].Rows[0].Value));
        var file = sections[3].Rows.ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("9.3 GB", file["Size"]);
        Assert.Equal("8 Mb/s", file["Bitrate"]);
        Assert.DoesNotContain(sections.SelectMany(s => s.Rows), r => r.Value.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Info_OfASeries_CountsItsRegularSeasonsAndEpisodes()
    {
        var series = Item(CatalogItemTypes.Series) with { FirstAirDate = "2021-01-08", SeasonCount = 3, EpisodeCount = 25 };
        Season[] seasons = [new() { SeasonNumber = 0, IsSpecials = true, EpisodeCount = 2 }, new() { SeasonNumber = 1, EpisodeCount = 10 }];

        var before = DetailFacts.InfoSections(series)[0].Rows.ToDictionary(r => r.Label, r => r.Value);
        var after = DetailFacts.InfoSections(series, seasons)[0].Rows.ToDictionary(r => r.Label, r => r.Value);

        Assert.Equal(("2021", "3 seasons", "25 episodes"), (before["Aired"], before["Seasons"], before["Episodes"]));
        Assert.Equal(("1 season", "10 episodes"), (after["Seasons"], after["Episodes"]));
        Assert.DoesNotContain("Runtime", before.Keys);
    }

    [Fact]
    public void Info_OfASeason_NamesItsNumberAndListedEpisodes()
    {
        var season = Item(CatalogItemTypes.Season) with { SeasonNumber = 3, AirDate = "2026-10-23" };

        var details = DetailFacts.InfoSections(season, listedEpisodes: 8)[0].Rows.ToDictionary(r => r.Label, r => r.Value);

        Assert.Equal(("Season 3", "8 episodes", "23 Oct 2026"), (details["Season"], details["Episodes"], details["Released"]));
    }

    [Fact]
    public void Info_OfAnEpisode_HasNoSeasonCounts_AndItsPlayedFileTakesTheAudioBadge()
    {
        var episode = Item(CatalogItemTypes.Episode) with
        {
            AirDate = "2026-10-23",
            OverlaySummary = new CatalogItemOverlay { Audio = "eac3" },
            UserData = new WatchRollup { LastFileId = "2" },
            Versions = [new FileVersion { FileId = "1", CodecAudio = "truehd" }, new FileVersion { FileId = "2", CodecAudio = "aac" }],
        };

        var sections = DetailFacts.InfoSections(episode);

        Assert.DoesNotContain(sections[0].Rows, r => r.Label is "Season" or "Seasons" or "Episodes");
        Assert.Equal(["TRUEHD", "EAC3"], sections.Where(s => s.Title.StartsWith("File", StringComparison.Ordinal)).Select(s => s.Rows.Single(r => r.Label == "Audio").Value));
    }

    [Fact]
    public void Info_OfAMovieWithoutCastOrCrew_HasNoSuchSections()
    {
        var movie = Item(CatalogItemTypes.Movie) with { Versions = [new FileVersion { FileId = "1", Resolution = "1080p" }] };

        Assert.Equal(["Details", "File"], DetailFacts.InfoSections(movie).Select(s => s.Title));
    }

    [Fact]
    public void Info_NumbersSeveralFiles_AndLeavesOutEmptySections()
    {
        var movie = Item(CatalogItemTypes.Movie) with
        {
            Genres = [],
            Versions = [new FileVersion { FileId = "1", Resolution = "2160p" }, new FileVersion { FileId = "2", Resolution = "1080p" }],
        };

        Assert.Equal(["Details", "File 1", "File 2"], DetailFacts.InfoSections(movie).Select(s => s.Title));
    }
}
