namespace Granary.Tests.Catalog;

/// <summary>
/// Response bodies from the server's contract fixtures (silo-server contracts/api/v2/fixtures, named per
/// constant) and, where a fixture leaves members out, built from the OpenAPI schemas.
/// </summary>
internal static class CatalogPayloads
{
    /// <summary><c>list_home_sections_ok.json</c>.</summary>
    public const string HomeSections = """
        {
          "sections": [
            {
              "id": "continue_watching",
              "section_type": "continue_watching",
              "title": "Continue Watching",
              "featured": false,
              "item_limit": 20,
              "total_count": 1,
              "is_custom": false,
              "customized": false,
              "items": [
                {
                  "content_id": "movie:heat-1995",
                  "type": "movie",
                  "title": "Heat",
                  "year": 1995,
                  "runtime": 170,
                  "genres": ["Crime"],
                  "keywords": [],
                  "content_rating": "R",
                  "status": "matched",
                  "rating_imdb": 8.3,
                  "position_seconds": 1200.5,
                  "duration_seconds": 10200,
                  "progress_updated_at": "2026-01-02T03:04:05.000Z",
                  "poster_url": "https://s3.example.test/poster.jpg",
                  "overlay_summary": { "resolution": "4K", "hdr": "Dolby Vision" },
                  "item_source": "in_progress",
                  "user_state": { "played": false, "is_favorite": false, "in_watchlist": true, "not_interested": false }
                }
              ]
            }
          ]
        }
        """;

    /// <summary>A next-up episode card and a series card with the optional members a fixture leaves out.</summary>
    public const string RecentlyAddedSection = """
        {
          "id": "default-recently-added-tv",
          "section_type": "recently_added",
          "title": "Recently Added TV",
          "featured": true,
          "item_limit": 100,
          "total_count": 240,
          "is_custom": false,
          "customized": true,
          "future_member": { "nested": [1, 2, 3] },
          "items": [
            {
              "content_id": "episode-tvdb-296762-1-5",
              "type": "episode",
              "title": "The You You Are",
              "status": "matched",
              "genres": [],
              "keywords": [],
              "series_id": "series-tvdb-296762",
              "series_title": "Severance",
              "season_number": 1,
              "episode_number": 5,
              "item_source": "next_up",
              "backdrop_url": "/api/v2/artwork/series/296762/backdrop/w1280.ab12.webp?exp=1767225600&sig=a-b_c",
              "backdrop_thumbhash": "3OcRJYB4d3h/iIeHeEh3eIhw+j2w",
              "backdrop_is_episode_still": true
            },
            {
              "content_id": "series-tvdb-371980",
              "type": "series",
              "title": "Andor",
              "status": "matched",
              "genres": ["Drama", "Sci-Fi & Fantasy"],
              "keywords": ["rebellion"],
              "overview": "The story of the burgeoning rebellion.",
              "release_date": "2022-09-21",
              "last_air_date": "2025-05-13",
              "show_status": "Ended",
              "content_rating": "TV-14",
              "rating_tmdb": 8.2,
              "added_at": "2026-03-04T05:06:07.890Z",
              "play_content_id": "episode-tvdb-371980-1-1",
              "poster_url": "https://cdn.example/andor.webp",
              "poster_thumbhash": "1QcSHQRnh493V4dIh4eXh1h4kJUI",
              "logo_url": "/api/v2/artwork/series/371980/logo/w500.cd34.webp?exp=1767225600&sig=xyz",
              "badges": ["new"],
              "studios": ["Lucasfilm"],
              "networks": ["Disney+"],
              "sort_metrics": { "release_date": "2022-09-21" },
              "user_state": { "played": false, "is_favorite": true, "in_watchlist": false, "not_interested": false },
              "overlay_summary": { "resolution": "4K", "hdr": "HDR10", "audio": "Atmos", "audio_channels": "7.1", "multi_audio": true, "multi_sub": true }
            },
            {
              "content_id": "podcast-episode-9",
              "type": "radio_play",
              "title": "A type this client has never seen",
              "status": "",
              "genres": [],
              "keywords": []
            }
          ]
        }
        """;

    /// <summary><c>list_catalog_items_ok.json</c>.</summary>
    public const string BrowsePage = """
        {
          "window_cursor": "eyJ2IjoxfQ.l3r0",
          "items": [
            { "content_id": "movie:heat-1995", "type": "movie", "title": "Heat", "year": 1995, "genres": ["Crime"], "keywords": [], "status": "matched" },
            { "content_id": "movie:alien-1979", "type": "movie", "title": "Heat", "year": 1995, "genres": ["Crime"], "keywords": [], "status": "matched" }
          ],
          "page": { "next_cursor": "eyJ2IjoxLCJwIjp7Im8iOjJ9fQ.48NB", "has_more": true },
          "total": 3,
          "total_exact": true,
          "effective_sort": { "field": "title", "order": "asc" }
        }
        """;

    public const string LastBrowsePage = """
        { "window_cursor": "w", "items": [], "page": { "has_more": false }, "total": 3, "total_exact": false }
        """;

    /// <summary><c>get_catalog_item_ok.json</c>: a sparse movie with members Granary does not model.</summary>
    public const string ItemFixture = """
        {
          "themes": { "owner_id": "movie:heat-1995", "items": [{ "id": "7", "title": "Opening", "duration_seconds": 60, "container": "mp3" }] },
          "content_id": "movie:heat-1995",
          "type": "movie",
          "title": "Heat",
          "year": 1995,
          "genres": ["Crime"],
          "keywords": [],
          "status": "",
          "overlay_summary": { "resolution": "4K" },
          "user_state": { "played": true, "is_favorite": true, "in_watchlist": false, "not_interested": false },
          "work_formats": [{ "type": "ebook", "content_id": "ebook:heat", "library_id": "2" }],
          "tagline": "A Los Angeles crime saga",
          "cast": [{ "name": "Al Pacino", "character": "Vincent Hanna", "order": 0, "person_id": "7" }],
          "crew": [],
          "user_data": { "watched_count": 1, "unplayed_count": 0, "in_progress_count": 0, "played": true, "last_file_id": "120" },
          "versions": [
            {
              "file_id": "120",
              "file_path": "/media/movies/Heat.mkv",
              "resolution": "2160p",
              "codec_video": "",
              "codec_audio": "",
              "hdr": false,
              "container": "",
              "file_size": 0,
              "duration": 0,
              "bitrate": 0,
              "added_at": "2026-01-02T03:04:05.678Z",
              "effective_audio_track_index": 1,
              "effective_audio_language": "en"
            }
          ],
          "rating_sources": [{ "source": "imdb", "score": 83, "votes": 673852 }],
          "ratings": [{ "source": "imdb", "name": "IMDb", "score": 83, "display": "8.3" }],
          "subtitles": []
        }
        """;

    /// <summary>A series detail with every member Granary models, built from the <c>CatalogItemDetail</c> schema.</summary>
    public const string SeriesDetail = """
        {
          "content_id": "series-tvdb-296762",
          "type": "series",
          "title": "Severance",
          "original_title": "Severance",
          "tagline": "Work, life, balance.",
          "overview": "Mark leads a team of office workers.",
          "status": "matched",
          "show_status": "Returning Series",
          "genres": ["Drama", "Mystery"],
          "keywords": [],
          "studios": ["Fifth Season"],
          "networks": ["Apple TV+"],
          "countries": ["US"],
          "first_air_date": "2022-02-18",
          "last_air_date": "2025-03-21",
          "year": 2022,
          "content_rating": "TV-MA",
          "season_count": 2,
          "episode_count": 19,
          "rating_imdb": 8.7,
          "rating_tmdb": 8.4,
          "backdrop_url": "/api/v2/artwork/series/296762/backdrop/w1280.ab12.webp?exp=1767225600&sig=s1",
          "logo_url": "https://cdn.example/severance-logo.png",
          "ratings": [
            { "source": "imdb", "name": "IMDb", "score": 87, "display": "8.7" },
            { "source": "tmdb", "name": "TMDB", "score": 84, "display": "8.4" },
            { "source": "rt_critic", "name": "Rotten Tomatoes", "score": 97, "display": "97%" }
          ],
          "cast": [
            { "name": "Adam Scott", "character": "Mark Scout", "order": 0, "person_id": "31", "photo_url": "/api/v2/artwork/people/31/w185.webp?exp=1&sig=p", "photo_thumbhash": "abc" },
            { "name": "Britt Lower", "character": "Helly R.", "order": 1 }
          ],
          "crew": [{ "name": "Dan Erickson", "job": "Creator", "person_id": "40" }],
          "user_state": { "played": false, "is_favorite": false, "in_watchlist": true, "not_interested": false },
          "user_data": {
            "played": false,
            "watched_count": 4,
            "unplayed_count": 15,
            "in_progress_count": 1,
            "is_in_progress": true,
            "position_seconds": 1312.25,
            "duration_seconds": 3420,
            "last_file_id": "205"
          },
          "play_content_id": "episode-tvdb-296762-1-5",
          "play_season_number": 1,
          "versions": [],
          "subtitles": [],
          "videos": [
            { "kind": "Trailer", "site": "YouTube", "site_key": "xEQP4VVuyrY", "is_official": true, "name": "Official Trailer", "language": "en" }
          ],
          "extras": [
            { "content_id": "extra-55", "kind": "behindthescenes", "title": "Making Lumon", "duration_seconds": 312, "file_id": 9001, "still_url": "/api/v2/artwork/extras/55/w400.webp?exp=1&sig=e" }
          ]
        }
        """;

    /// <summary>A movie version with real values: <c>bitrate</c> in kbps, <c>duration</c> in seconds.</summary>
    public const string MovieWithVersion = """
        {
          "content_id": "movie-tmdb-949",
          "type": "movie",
          "title": "Heat",
          "status": "matched",
          "genres": ["Crime"],
          "keywords": [],
          "runtime": 170,
          "cast": [],
          "crew": [],
          "ratings": [],
          "subtitles": [],
          "versions": [
            {
              "file_id": "120",
              "file_name": "Heat (1995) {edition-Director's Cut}.mkv",
              "resolution": "2160p",
              "codec_video": "hevc",
              "codec_audio": "truehd",
              "container": "mkv",
              "hdr": true,
              "file_size": 78123456789,
              "duration": 10247,
              "bitrate": 61000,
              "added_at": "2026-01-02T03:04:05.678Z",
              "edition_key": "directors-cut",
              "edition_raw": "Director's Cut",
              "audio_tracks": [{ "index": 1, "language": "en" }],
              "intro": { "start": 0, "end": 42.5 }
            }
          ]
        }
        """;

    /// <summary><c>list_series_seasons_ok.json</c>.</summary>
    public const string Seasons = """
        {
          "items": [
            {
              "content_id": "series:severance-S01",
              "play_content_id": "episode:severance-s01e02",
              "season_number": 1,
              "title": "Season 1",
              "air_date": "2022-02-18",
              "episode_count": 9,
              "poster_url": "https://images.example/season.webp",
              "poster_thumbhash": "placeholder",
              "user_data": { "watched_count": 1, "unplayed_count": 8, "in_progress_count": 0, "played": false }
            }
          ]
        }
        """;

    /// <summary><c>list_season_episodes_ok.json</c>.</summary>
    public const string Episodes = """
        {
          "items": [
            {
              "content_id": "episode:severance-s01e01",
              "season_number": 1,
              "episode_number": 1,
              "title": "Good News About Hell",
              "air_date": "2022-02-18",
              "runtime": 57,
              "ratings": [
                { "source": "imdb", "name": "IMDb", "score": 87, "display": "8.7" },
                { "source": "tmdb", "name": "TMDB", "score": 82.5, "display": "8.3" }
              ],
              "user_data": { "watched_count": 1, "unplayed_count": 0, "in_progress_count": 0, "played": true },
              "files": [{ "file_id": "201", "resolution": "1080p", "hdr": false, "file_size": 1 }]
            },
            {
              "content_id": "episode:severance-s01e02",
              "season_number": 1,
              "episode_number": 2,
              "title": "Good News About Hell",
              "air_date": "2022-02-18",
              "runtime": 57,
              "user_data": { "watched_count": 1, "unplayed_count": 0, "in_progress_count": 0, "played": false },
              "files": [{ "file_id": "202", "resolution": "1080p", "hdr": false, "file_size": 1 }]
            }
          ]
        }
        """;

    /// <summary><c>list_people_ok.json</c>.</summary>
    public const string People = """
        {
          "items": [
            {
              "id": "7",
              "name": "Al Pacino",
              "bio": "Actor",
              "birth_date": "1940-04-25",
              "birthplace": "New York",
              "photo_url": "https://cdn.example/people/7.jpg",
              "tmdb_id": "1158"
            }
          ]
        }
        """;

    /// <summary><c>get_catalog_search_capabilities_ok.json</c>.</summary>
    public const string SearchCapabilities = """
        {
          "revision": "9cc6c59e878668a6410bba1c347d11c8d7fbafd7ef5684f84089fc56cb25f86b",
          "state": "available",
          "allowed": true,
          "people_media_scope": true,
          "facet_value_search": true,
          "person_prefetch": true,
          "video_with_episodes_scope": true,
          "extended_query_rules": true,
          "show_title_rules": true,
          "letter_index": true,
          "provider": "meilisearch",
          "result_window_limit": 1000,
          "session_ttl_seconds": 900,
          "max_sessions_per_account": 16
        }
        """;

    /// <summary><c>get_image_capabilities_ok.json</c>.</summary>
    public const string ImageCapabilities = """
        {
          "revision": "46818c56abe31ba9199327373db4f736261c04d81c0dee7d5a3c5210cd8cb0d7",
          "state": "available",
          "allowed": true,
          "episode_still_provenance": true,
          "extended_item_artwork": true,
          "season_list_artwork_param": "include_artwork",
          "param": "image_size",
          "slot_width_params": {
            "backdrop": "backdrop_width",
            "logo": "logo_width",
            "photo": "photo_width",
            "poster": "poster_width",
            "still": "still_width"
          },
          "sizes": ["small", "medium", "large", "original"],
          "widths": {
            "backdrop": { "small": 300, "medium": 1920, "large": 2560 },
            "logo": { "small": 500, "medium": 500, "large": 1280 },
            "poster": { "small": 300, "medium": 500, "large": 1000 },
            "profile": { "small": 300, "medium": 500, "large": 500 },
            "still": { "small": 300, "medium": 500, "large": 1280 }
          },
          "original_max_width_px": 3840,
          "storage_backend": "local",
          "delivery": "server"
        }
        """;

    /// <summary><c>get_catalog_filters_ok.json</c>.</summary>
    public const string Filters = """
        {
          "genres": ["Crime"],
          "studios": [],
          "networks": [],
          "countries": [],
          "original_languages": [],
          "content_ratings": [],
          "authors": ["Frank Herbert"],
          "narrators": [],
          "series": [],
          "technical": { "resolutions": ["2160p"], "audio_languages": [], "subtitle_languages": ["en"] }
        }
        """;

    /// <summary><c>user_libraries.json</c>.</summary>
    public const string Libraries = """
        { "items": [{ "id": "12", "name": "Movies", "type": "movies", "sort_order": 2, "poster_url": "https://images.example/poster" }] }
        """;

    /// <summary><c>list_similar_ok.json</c>, shortened.</summary>
    public const string Similar = """
        { "items": [{ "content_id": "movie:heat-1995", "type": "movie", "title": "Heat", "year": 1995, "genres": ["Crime"], "keywords": [], "status": "matched" }] }
        """;
}
