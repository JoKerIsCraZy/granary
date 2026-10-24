namespace Granary.Tests.Browse;

/// <summary>
/// Detail responses shaped like the titles of the web client screenshots (docs/ui-design.md), built from the
/// <c>CatalogItemDetail</c>, <c>Season</c> and <c>Episode</c> schemas. Image members are root-relative and
/// signed like the server's.
/// </summary>
internal static class ItemDetailPayloads
{
    public const string MovieId = "movie-tmdb-1001";
    public const string SeriesId = "series-tmdb-96677";
    public const string Season3Id = "season-96677-3";
    public const string EpisodeId = "episode-tmdb-96677-3-1";
    public const string Episode2Id = "episode-tmdb-96677-3-2";

    /// <summary>A movie in progress with one version, a YouTube trailer and a Vimeo clip; the file path is admin data.</summary>
    public const string Movie = """
        {
          "content_id": "movie-tmdb-1001",
          "type": "movie",
          "title": "Masterplan",
          "status": "matched",
          "genres": ["Action", "Thriller"],
          "keywords": [],
          "year": 2026,
          "release_date": "2026-03-20",
          "runtime": 110,
          "content_rating": "R",
          "tagline": "One plan. No second chances.",
          "overview": "A former soldier takes one last job.",
          "studios": ["Amazon MGM Studios", "Cedar Park"],
          "poster_url": "/api/v2/artwork/movies/1001/poster/w342.ab12.webp?exp=1767225600&sig=poster-sig",
          "backdrop_url": "/api/v2/artwork/movies/1001/backdrop/w1280.cd34.webp?exp=1767225600&sig=backdrop-sig",
          "logo_url": "/api/v2/artwork/movies/1001/logo/w500.ef56.webp?exp=1767225600&sig=logo-sig",
          "overlay_summary": { "resolution": "1080p", "audio": "eac3", "audio_channels": "5.1" },
          "ratings": [
            { "source": "imdb", "name": "IMDb", "score": 54, "display": "5.4" },
            { "source": "tmdb", "name": "TMDB", "score": 71, "display": "7.1" }
          ],
          "cast": [
            { "name": "Jason Statham", "character": "Levon Cade", "order": 0, "person_id": "976", "photo_url": "/api/v2/artwork/people/976/w185.webp?exp=1&sig=c" },
            { "name": "Jason Flemyng", "character": "Gareth", "order": 2, "person_id": "978" },
            { "name": "Arianna Rivas", "character": "Jenny", "order": 1 }
          ],
          "crew": [
            { "name": "David Ayer", "job": "Director", "person_id": "19769" },
            { "name": "Sylvester Stallone", "job": "Screenplay", "person_id": "16483" },
            { "name": "David Ayer", "job": "Writer", "person_id": "19769" },
            { "name": "Chuck Dixon", "job": "Writer", "person_id": "1000" },
            { "name": "Jane Doe", "job": "Producer" }
          ],
          "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false },
          "user_data": {
            "played": false,
            "watched_count": 0,
            "unplayed_count": 1,
            "in_progress_count": 1,
            "is_in_progress": true,
            "position_seconds": 1312.5,
            "duration_seconds": 6545,
            "last_file_id": "301"
          },
          "versions": [
            {
              "file_id": "301",
              "file_path": "/media/movies/Masterplan (2026)/Masterplan.mkv",
              "file_name": "Masterplan.mkv",
              "resolution": "1080p",
              "codec_video": "h264",
              "codec_audio": "eac3",
              "container": "mkv",
              "hdr": false,
              "file_size": 4123456789,
              "duration": 6545,
              "bitrate": 8000
            }
          ],
          "videos": [
            { "kind": "Trailer", "site": "YouTube", "site_key": "dQw4w9WgXcQ", "is_official": true, "name": "Official Trailer", "language": "en" },
            { "kind": "Clip", "site": "Vimeo", "site_key": "76979871", "is_official": false, "name": "Vimeo clip" }
          ],
          "subtitles": []
        }
        """;

    public const string MovieSimilar = """
        {
          "items": [
            { "content_id": "movie-tmdb-1002", "type": "movie", "title": "Beekeeper", "year": 2024, "genres": ["Action"], "keywords": [], "status": "matched",
              "poster_url": "/api/v2/artwork/movies/1002/poster/w342.webp?exp=1&sig=s" },
            { "content_id": "movie-tmdb-1001", "type": "movie", "title": "Masterplan", "year": 2026, "genres": [], "keywords": [], "status": "matched" },
            { "content_id": "series-tmdb-5", "type": "series", "title": "Reacher", "year": 2022, "genres": [], "keywords": [], "status": "matched" }
          ]
        }
        """;

    /// <summary>A series with two creators, four ratings, an episode in progress, a favourite on the watchlist.</summary>
    public const string Series = """
        {
          "content_id": "series-tmdb-96677",
          "type": "series",
          "title": "Lupin",
          "status": "matched",
          "show_status": "Returning Series",
          "genres": ["Crime", "Drama", "Mystery"],
          "keywords": [],
          "year": 2021,
          "first_air_date": "2021-01-08",
          "last_air_date": "2026-10-23",
          "content_rating": "TV-MA",
          "tagline": "Every story has its gentleman thief.",
          "overview": "Inspired by the adventures of Arsène Lupin, gentleman thief Assane Diop sets out to avenge his father.",
          "networks": ["Netflix"],
          "season_count": 3,
          "episode_count": 25,
          "poster_url": "/api/v2/artwork/series/96677/poster/w342.webp?exp=1&sig=p",
          "backdrop_url": "/api/v2/artwork/series/96677/backdrop/w1280.webp?exp=1&sig=b",
          "logo_url": "https://cdn.example/lupin-logo.png",
          "ratings": [
            { "source": "imdb", "name": "IMDb", "score": 75, "display": "7.5" },
            { "source": "tmdb", "name": "TMDB", "score": 77, "display": "7.7" },
            { "source": "rt_critic", "name": "Rotten Tomatoes", "score": 98, "display": "98%" },
            { "source": "metacritic", "name": "Metacritic", "score": 75, "display": "75" }
          ],
          "cast": [{ "name": "Omar Sy", "character": "Assane Diop", "order": 0, "person_id": "1" }],
          "crew": [
            { "name": "George Kay", "job": "Creator", "person_id": "50" },
            { "name": "François Uzan", "job": "Creator", "person_id": "51" },
            { "name": "Louis Leterrier", "job": "Director", "person_id": "52" }
          ],
          "user_state": { "played": false, "is_favorite": true, "in_watchlist": true, "not_interested": false },
          "user_data": {
            "played": false,
            "watched_count": 12,
            "unplayed_count": 13,
            "in_progress_count": 1,
            "is_in_progress": true,
            "position_seconds": 600,
            "duration_seconds": 3120
          },
          "play_content_id": "episode-tmdb-96677-3-1",
          "play_season_number": 3,
          "versions": [],
          "videos": [],
          "subtitles": []
        }
        """;

    /// <summary>Out of order, with the specials first, as a server may list them.</summary>
    public const string SeriesSeasons = """
        {
          "items": [
            { "content_id": "season-96677-0", "season_number": 0, "title": "Specials", "episode_count": 2, "is_specials": true,
              "user_data": { "played": false, "watched_count": 0, "unplayed_count": 2, "in_progress_count": 0 } },
            { "content_id": "season-96677-2", "season_number": 2, "title": "Staffel 2", "episode_count": 7,
              "user_data": { "played": true, "watched_count": 7, "unplayed_count": 0, "in_progress_count": 0 } },
            { "content_id": "season-96677-1", "season_number": 1, "title": "Staffel 1", "episode_count": 10,
              "poster_url": "/api/v2/artwork/seasons/1/w342.webp?exp=1&sig=s1",
              "user_data": { "played": true, "watched_count": 10, "unplayed_count": 0, "in_progress_count": 0 } },
            { "content_id": "season-96677-3", "season_number": 3, "title": "Staffel 3", "episode_count": 8, "play_content_id": "episode-tmdb-96677-3-1",
              "user_data": { "played": false, "watched_count": 4, "unplayed_count": 4, "in_progress_count": 1 } }
          ]
        }
        """;

    public const string SingleSeason = """
        {
          "items": [
            { "content_id": "season-96677-1", "season_number": 1, "title": "Season 1", "episode_count": 2,
              "user_data": { "played": false, "watched_count": 0, "unplayed_count": 2, "in_progress_count": 0 } }
          ]
        }
        """;

    public const string SeriesSimilar = """
        { "items": [{ "content_id": "series-tmdb-7", "type": "series", "title": "Money Heist", "year": 2017, "genres": [], "keywords": [], "status": "matched" }] }
        """;

    /// <summary>A localized season name ("Staffel 3") under the series title.</summary>
    public const string Season = """
        {
          "content_id": "season-96677-3",
          "type": "season",
          "title": "Staffel 3",
          "status": "matched",
          "genres": [],
          "keywords": [],
          "series_id": "series-tmdb-96677",
          "series_title": "Lupin",
          "season_number": 3,
          "air_date": "2026-10-23",
          "episode_count": 8,
          "overview": "Assane faces his most personal heist yet.",
          "poster_url": "/api/v2/artwork/seasons/3/w342.webp?exp=1&sig=s3",
          "backdrop_url": "/api/v2/artwork/series/96677/backdrop/w1280.webp?exp=1&sig=b",
          "cast": [{ "name": "Omar Sy", "character": "Assane Diop", "order": 0 }],
          "crew": [{ "name": "George Kay", "job": "Creator" }],
          "user_data": { "played": false, "watched_count": 0, "unplayed_count": 8, "in_progress_count": 0 },
          "play_content_id": "episode-tmdb-96677-3-1",
          "versions": [],
          "subtitles": []
        }
        """;

    public const string SeasonEpisodes = """
        {
          "items": [
            {
              "content_id": "episode-tmdb-96677-3-1",
              "season_number": 3,
              "episode_number": 1,
              "title": "Chapter 1",
              "air_date": "2026-10-23",
              "runtime": 52,
              "overview": "Assane returns to Paris.",
              "still_url": "/api/v2/artwork/episodes/1/w400.webp?exp=1&sig=e1",
              "overlay_summary": { "resolution": "1080p", "audio": "eac3" },
              "user_data": { "played": true, "watched_count": 1, "unplayed_count": 0, "in_progress_count": 0 },
              "files": [{ "file_id": "901", "resolution": "1080p", "hdr": false, "file_size": 1 }]
            },
            {
              "content_id": "episode-tmdb-96677-3-2",
              "season_number": 3,
              "episode_number": 2,
              "title": "Chapter 2",
              "air_date": "2026-10-23",
              "runtime": 48,
              "user_data": { "played": false, "watched_count": 0, "unplayed_count": 1, "in_progress_count": 1, "position_seconds": 1440, "duration_seconds": 2880 },
              "files": [{ "file_id": "902", "resolution": "1080p", "hdr": false, "file_size": 1 }]
            }
          ]
        }
        """;

    /// <summary>A watched episode with two versions; its still is the backdrop, its logo is the series'.</summary>
    public const string Episode = """
        {
          "content_id": "episode-tmdb-96677-3-1",
          "type": "episode",
          "title": "Chapter 1",
          "status": "matched",
          "genres": ["Crime"],
          "keywords": [],
          "series_id": "series-tmdb-96677",
          "series_title": "Lupin",
          "season_number": 3,
          "episode_number": 1,
          "air_date": "2026-10-23",
          "runtime": 52,
          "overview": "Assane returns to Paris.",
          "backdrop_url": "/api/v2/artwork/episodes/1/still/w1280.webp?exp=1&sig=still",
          "backdrop_is_episode_still": true,
          "logo_url": "https://cdn.example/lupin-logo.png",
          "ratings": [{ "source": "imdb", "name": "IMDb", "score": 81, "display": "8.1" }],
          "cast": [
            { "name": "Omar Sy", "character": "Assane Diop", "order": 0, "person_id": "1" },
            { "name": "Guest Star", "character": "Inspector", "order": 7 }
          ],
          "crew": [
            { "name": "Ludovic Bernard", "job": "Director" },
            { "name": "George Kay", "job": "Writer", "person_id": "50" }
          ],
          "user_state": { "played": true, "is_favorite": false, "in_watchlist": false, "not_interested": false },
          "user_data": { "played": true, "watched_count": 1, "unplayed_count": 0, "in_progress_count": 0 },
          "versions": [
            { "file_id": "901", "resolution": "2160p", "codec_video": "hevc", "codec_audio": "truehd", "container": "mkv", "hdr": true,
              "file_size": 9000000000, "duration": 3125, "bitrate": 24000, "file_path": "/media/tv/Lupin/S03E01.mkv" },
            { "file_id": "902", "resolution": "1080p", "codec_video": "h264", "codec_audio": "eac3", "container": "mp4", "hdr": false,
              "file_size": 2000000000, "duration": 3125, "bitrate": 6000, "edition_raw": "Director's Cut" }
          ],
          "videos": [],
          "subtitles": []
        }
        """;
}
