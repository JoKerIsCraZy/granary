using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;

namespace Granary.Tests.Catalog;

public sealed class CatalogModelTests
{
    private static T Read<T>(string json, JsonTypeInfo<T> type) => JsonSerializer.Deserialize(json, type)!;

    [Fact]
    public void HomeSections_ReadAContinueWatchingCard()
    {
        var sections = Read(CatalogPayloads.HomeSections, SiloJsonContext.Default.SectionCollection);

        var row = Assert.Single(sections.Sections);
        Assert.Equal(("continue_watching", SectionTypes.ContinueWatching, "Continue Watching"), (row.Id, row.SectionType, row.Title));
        Assert.Equal((20, 1, false, false, false), (row.ItemLimit, row.TotalCount, row.Featured, row.IsCustom, row.Customized));
        var card = Assert.Single(row.Items);
        Assert.Equal(("movie:heat-1995", CatalogItemTypes.Movie, "Heat", "matched"), (card.ContentId, card.Type, card.Title, card.Status));
        Assert.Equal(["Crime"], card.Genres);
        Assert.Equal((1995, 170, "R", 8.3), (card.Year, card.Runtime, card.ContentRating, card.RatingImdb));
        Assert.Equal(1200.5, card.PositionSeconds);
        Assert.Equal(10200, card.DurationSeconds);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), card.ProgressUpdatedAt);
        Assert.Equal(ItemSources.InProgress, card.ItemSource);
        Assert.Equal("https://s3.example.test/poster.jpg", card.PosterUrl);
        Assert.Equal(("4K", "Dolby Vision"), (card.OverlaySummary?.Resolution, card.OverlaySummary?.Hdr));
        Assert.Equal(new CatalogItemUserState(Played: false, IsFavorite: false, InWatchlist: true, NotInterested: false), card.UserState);
    }

    [Fact]
    public void Section_ReadsOptionalCardMembersAndToleratesUnknownOnes()
    {
        var row = Read(CatalogPayloads.RecentlyAddedSection, SiloJsonContext.Default.Section);

        Assert.Equal((240, true, true), (row.TotalCount, row.Featured, row.Customized));
        Assert.Equal(3, row.Items.Count);

        var episode = row.Items[0];
        Assert.Equal(("series-tvdb-296762", "Severance", 1, 5), (episode.SeriesId, episode.SeriesTitle, episode.SeasonNumber, episode.EpisodeNumber));
        Assert.Equal(ItemSources.NextUp, episode.ItemSource);
        Assert.Null(episode.ProgressUpdatedAt);
        Assert.StartsWith("/api/v2/artwork/series/296762/backdrop/", episode.BackdropUrl, StringComparison.Ordinal);
        Assert.Equal("3OcRJYB4d3h/iIeHeEh3eIhw+j2w", episode.BackdropThumbhash);
        Assert.Empty(episode.Badges);
        Assert.Empty(episode.Studios);
        Assert.Null(episode.UserState);

        var series = row.Items[1];
        Assert.Equal(("2022-09-21", "2025-05-13", "Ended"), (series.ReleaseDate, series.LastAirDate, series.ShowStatus));
        Assert.Equal(("TV-14", 8.2), (series.ContentRating, series.RatingTmdb));
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, 890, TimeSpan.Zero), series.AddedAt);
        Assert.Equal("episode-tvdb-371980-1-1", series.PlayContentId);
        Assert.Equal(("1QcSHQRnh493V4dIh4eXh1h4kJUI", "/api/v2/artwork/series/371980/logo/w500.cd34.webp?exp=1767225600&sig=xyz"),
            (series.PosterThumbhash, series.LogoUrl));
        Assert.Equal(["new"], series.Badges);
        Assert.Equal(["Lucasfilm"], series.Studios);
        Assert.Equal(["Disney+"], series.Networks);
        Assert.Equal("The story of the burgeoning rebellion.", series.Overview);
        Assert.True(series.UserState?.IsFavorite);
        Assert.Equal(("Atmos", "7.1", true, true),
            (series.OverlaySummary?.Audio, series.OverlaySummary?.AudioChannels, series.OverlaySummary?.MultiAudio, series.OverlaySummary?.MultiSub));

        Assert.Equal("radio_play", row.Items[2].Type);
    }

    [Fact]
    public void Browse_ReadsThePageAndItsCursor()
    {
        var page = Read(CatalogPayloads.BrowsePage, SiloJsonContext.Default.CatalogBrowseCollection);
        var last = Read(CatalogPayloads.LastBrowsePage, SiloJsonContext.Default.CatalogBrowseCollection);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal((3, true, "eyJ2IjoxfQ.l3r0"), (page.Total, page.TotalExact, page.WindowCursor));
        Assert.Equal(new CatalogEffectiveSort("title", "asc"), page.EffectiveSort);
        Assert.Equal("eyJ2IjoxLCJwIjp7Im8iOjJ9fQ.48NB", page.NextCursor);
        Assert.Null(last.NextCursor);
        Assert.False(last.TotalExact);
        Assert.Null(last.EffectiveSort);
    }

    [Fact]
    public void Detail_ReadsTheFixtureAndSkipsUnmodelledMembers()
    {
        var item = Read(CatalogPayloads.ItemFixture, SiloJsonContext.Default.CatalogItemDetail);

        Assert.Equal(("movie:heat-1995", "A Los Angeles crime saga"), (item.ContentId, item.Tagline));
        var cast = Assert.Single(item.Cast);
        Assert.Equal(new CastCredit("Al Pacino", "Vincent Hanna", 0, "7", null, null), cast);
        Assert.Empty(item.Crew);
        Assert.Equal((true, 1, 0, 0, "120"), (item.UserData?.Played, item.UserData?.WatchedCount, item.UserData?.UnplayedCount,
            item.UserData?.InProgressCount, item.UserData?.LastFileId));
        var version = Assert.Single(item.Versions);
        Assert.Equal(("120", "2160p", false), (version.FileId, version.Resolution, version.Hdr));
        Assert.Equal(new CatalogRating("imdb", "IMDb", 83, "8.3"), Assert.Single(item.Ratings));
        Assert.Empty(item.Videos);
        Assert.Empty(item.Extras);
        Assert.Empty(item.Countries);
    }

    [Fact]
    public void Detail_ReadsASeriesPage()
    {
        var series = Read(CatalogPayloads.SeriesDetail, SiloJsonContext.Default.CatalogItemDetail);

        Assert.Equal(("Severance", "Work, life, balance.", "Returning Series"), (series.OriginalTitle, series.Tagline, series.ShowStatus));
        Assert.Equal(["US"], series.Countries);
        Assert.Equal(["Apple TV+"], series.Networks);
        Assert.Equal(("2022-02-18", "2025-03-21", 2, 19), (series.FirstAirDate, series.LastAirDate, series.SeasonCount, series.EpisodeCount));
        Assert.Equal(["imdb", "tmdb", "rt_critic"], series.Ratings.Select(r => r.Source));
        Assert.Equal("97%", series.Ratings[2].Display);
        Assert.Equal(2, series.Cast.Count);
        Assert.Equal(("31", "abc"), (series.Cast[0].PersonId, series.Cast[0].PhotoThumbhash));
        Assert.Null(series.Cast[1].PhotoUrl);
        Assert.Equal(new CrewCredit("Dan Erickson", "Creator", "40", null, null), Assert.Single(series.Crew));
        Assert.Equal(("episode-tvdb-296762-1-5", 1), (series.PlayContentId, series.PlaySeasonNumber));
        Assert.Equal((true, 1312.25, 3420.0, "205"), (series.UserData?.IsInProgress, series.UserData?.PositionSeconds,
            series.UserData?.DurationSeconds, series.UserData?.LastFileId));
        Assert.True(series.UserState?.InWatchlist);
        Assert.Empty(series.Versions);
        Assert.Equal(new ItemVideo("Trailer", "YouTube", "xEQP4VVuyrY", true, "Official Trailer", "en"), Assert.Single(series.Videos));
        var extra = Assert.Single(series.Extras);
        Assert.Equal(("extra-55", "behindthescenes", "Making Lumon", 312L, 9001L), (extra.ContentId, extra.Kind, extra.Title, extra.DurationSeconds, extra.FileId));
    }

    [Fact]
    public void Detail_KeepsTheVersionUnitsOfTheWire()
    {
        var movie = Read(CatalogPayloads.MovieWithVersion, SiloJsonContext.Default.CatalogItemDetail);

        var version = Assert.Single(movie.Versions);
        Assert.Equal(170, movie.Runtime);
        Assert.Equal(10247, version.Duration);
        Assert.Equal(61000, version.Bitrate);
        Assert.Equal(78123456789, version.FileSize);
        Assert.Equal(("hevc", "truehd", "mkv", true), (version.CodecVideo, version.CodecAudio, version.Container, version.Hdr));
        Assert.Equal(("directors-cut", "Director's Cut"), (version.EditionKey, version.EditionRaw));
        Assert.Equal("Heat (1995) {edition-Director's Cut}.mkv", version.FileName);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero), version.AddedAt);
    }

    [Fact]
    public void SeasonsAndEpisodes_ReadTheFixtures()
    {
        var seasons = Read(CatalogPayloads.Seasons, SiloJsonContext.Default.SeasonCollection);
        var episodes = Read(CatalogPayloads.Episodes, SiloJsonContext.Default.EpisodeCollection);

        var season = Assert.Single(seasons.Items);
        Assert.Equal(("series:severance-S01", 1, "Season 1", 9), (season.ContentId, season.SeasonNumber, season.Title, season.EpisodeCount));
        Assert.Equal(("episode:severance-s01e02", "2022-02-18", "placeholder"), (season.PlayContentId, season.AirDate, season.PosterThumbhash));
        Assert.Equal((false, 1, 8), (season.UserData?.Played, season.UserData?.WatchedCount, season.UserData?.UnplayedCount));
        Assert.Null(seasons.Page);

        Assert.Equal(2, episodes.Items.Count);
        var first = episodes.Items[0];
        Assert.Equal((1, 1, "Good News About Hell", 57), (first.SeasonNumber, first.EpisodeNumber, first.Title, first.Runtime));
        Assert.Equal(82.5, first.Ratings[1].Score);
        Assert.True(first.UserData?.Played);
        Assert.Equal(("201", "1080p", 1L), (first.Files[0].FileId, first.Files[0].Resolution, first.Files[0].FileSize));
        Assert.Empty(episodes.Items[1].Ratings);
        Assert.Null(episodes.Items[1].StillUrl);
    }

    [Fact]
    public void PeopleAndLibraries_ReadTheFixtures()
    {
        var people = Read(CatalogPayloads.People, SiloJsonContext.Default.PersonCollection);
        var libraries = Read(CatalogPayloads.Libraries, SiloJsonContext.Default.LibraryCollection);

        var person = Assert.Single(people.Items);
        Assert.Equal(("7", "Al Pacino", "https://cdn.example/people/7.jpg"), (person.Id, person.Name, person.PhotoUrl));
        Assert.Equal(("Actor", "1940-04-25", "New York", null), (person.Bio, person.BirthDate, person.Birthplace, person.DeathDate));
        Assert.Equal(new Library("12", "Movies", "movies", 2, "https://images.example/poster"), Assert.Single(libraries.Items));
    }

    [Fact]
    public void Capabilities_ReadTheFixtures()
    {
        var search = Read(CatalogPayloads.SearchCapabilities, SiloJsonContext.Default.CatalogSearchCapabilities);
        var images = Read(CatalogPayloads.ImageCapabilities, SiloJsonContext.Default.ImageCapabilities);

        Assert.True(search.IsAvailable);
        Assert.Equal(("meilisearch", true, true, 1000, 900), (search.Provider, search.PeopleMediaScope, search.VideoWithEpisodesScope,
            search.ResultWindowLimit, search.SessionTtlSeconds));
        Assert.True(images.IsAvailable);
        Assert.Equal(("image_size", "include_artwork", 3840), (images.Param, images.SeasonListArtworkParam, images.OriginalMaxWidthPx));
        Assert.Equal(["small", "medium", "large", "original"], images.Sizes);
        Assert.Equal("poster_width", images.SlotWidthParams[ImageSlots.Poster]);
        Assert.Equal(5, images.SlotWidthParams.Count);
        Assert.Equal(new ImageSizeWidths(300, 1920, 2560), images.Widths["backdrop"]);
        Assert.Equal(("local", "server"), (images.StorageBackend, images.Delivery));
    }

    [Fact]
    public void ExplicitNulls_BecomeEmptyValues()
    {
        const string json = """
            {
              "content_id": "movie-1", "type": "movie", "title": null, "status": "matched",
              "genres": null, "keywords": [], "badges": null, "studios": null,
              "cast": [], "crew": [], "ratings": null, "versions": [], "subtitles": [], "videos": null
            }
            """;

        var item = Read(json, SiloJsonContext.Default.CatalogItemDetail);

        Assert.Equal("", item.Title);
        Assert.Empty(item.Genres);
        Assert.Empty(item.Badges);
        Assert.Empty(item.Studios);
        Assert.Empty(item.Ratings);
        Assert.Empty(item.Videos);
        Assert.Empty(item.Extras);
    }

    [Fact]
    public void Browse_WithMoreButNoCursor_HasNoNextPage()
    {
        var page = Read("""{ "items": [], "page": { "has_more": true, "next_cursor": "" }, "total": 0, "total_exact": true, "window_cursor": "w" }""",
            SiloJsonContext.Default.CatalogBrowseCollection);

        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void Filters_ReadTheFixture()
    {
        var filters = Read(CatalogPayloads.Filters, SiloJsonContext.Default.CatalogFilters);

        Assert.Equal(["Crime"], filters.Genres);
        Assert.Empty(filters.ContentRatings);
        Assert.Equal(["2160p"], filters.Technical?.Resolutions);
        Assert.Equal(["en"], filters.Technical?.SubtitleLanguages);
    }
}
