namespace Granary.Tests.Downloads;

/// <summary>
/// A film, a season of "Example Series" with three episodes (the sixth without a file), its fifth episode, and the series with
/// its specials and a second season, as the catalog answers them, for the title page's and the cards' Download items.
/// </summary>
internal static class TitlePayloads
{
    public const string Movie = """
        {
          "content_id": "mv_1", "type": "movie", "title": "Example Movie", "status": "matched", "genres": [], "keywords": [],
          "year": 2026, "runtime": 120,
          "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false },
          "versions": [
            { "file_id": "4567", "resolution": "1080p", "codec_video": "h264", "codec_audio": "eac3", "container": "mkv", "hdr": false,
              "file_size": 8589934592, "duration": 7200, "bitrate": 9000 }
          ],
          "videos": [], "subtitles": []
        }
        """;

    /// <summary>The film before its file arrived: the server lists no version.</summary>
    public const string MovieWithoutFiles = """
        {
          "content_id": "mv_1", "type": "movie", "title": "Example Movie", "status": "matched", "genres": [], "keywords": [],
          "year": 2026, "runtime": 120,
          "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false },
          "versions": [], "videos": [], "subtitles": []
        }
        """;

    public const string Season = """
        {
          "content_id": "se_1", "type": "season", "title": "Season 1", "status": "matched", "genres": [], "keywords": [],
          "series_id": "sr_1", "series_title": "Example Series", "season_number": 1, "episode_count": 2,
          "versions": [], "subtitles": []
        }
        """;

    public const string SeasonEpisodes = """
        {
          "items": [
            { "content_id": "ep_1", "season_number": 1, "episode_number": 1, "title": "Chapter One", "runtime": 50,
              "files": [
                { "file_id": "4570", "resolution": "720p", "codec_video": "h264", "hdr": false, "file_size": 1073741824 },
                { "file_id": "4571", "resolution": "1080p", "codec_video": "h264", "hdr": false, "file_size": 2147483648 },
                { "file_id": "4572", "resolution": "1080p", "hdr": false, "file_size": 4294967296, "unreadable": true }
              ] },
            { "content_id": "ep_5", "season_number": 1, "episode_number": 5, "title": "Chapter Five", "runtime": 50,
              "files": [{ "file_id": "4575", "resolution": "1080p", "codec_video": "h264", "hdr": false, "file_size": 2147483648 }] },
            { "content_id": "ep_6", "season_number": 1, "episode_number": 6, "title": "Chapter Six", "runtime": 50, "files": [] }
          ]
        }
        """;

    /// <summary>A season whose episodes have no file yet.</summary>
    public const string SeasonEpisodesWithoutFiles = """
        {
          "items": [
            { "content_id": "ep_6", "season_number": 1, "episode_number": 6, "title": "Chapter Six", "runtime": 50, "files": [] }
          ]
        }
        """;

    /// <summary>"Example Series" itself, whose seasons are <see cref="SeriesSeasons"/>.</summary>
    public const string Series = """
        {
          "content_id": "sr_1", "type": "series", "title": "Example Series", "status": "matched", "genres": [], "keywords": [],
          "year": 2026,
          "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false },
          "versions": [], "videos": [], "subtitles": []
        }
        """;

    /// <summary>The specials first, as the server lists them, then two seasons: the first's episodes are <see cref="SeasonEpisodes"/>.</summary>
    public const string SeriesSeasons = """
        {
          "items": [
            { "content_id": "se_0", "season_number": 0, "title": "Specials", "episode_count": 1, "is_specials": true },
            { "content_id": "se_1", "season_number": 1, "title": "Season 1", "episode_count": 3 },
            { "content_id": "se_2", "season_number": 2, "title": "Season 2", "episode_count": 1 }
          ]
        }
        """;

    public const string SpecialsEpisodes = """
        {
          "items": [
            { "content_id": "sp_1", "season_number": 0, "episode_number": 1, "title": "Behind the Scenes", "runtime": 20,
              "files": [{ "file_id": "4590", "resolution": "1080p", "codec_video": "h264", "hdr": false, "file_size": 1073741824 }] }
          ]
        }
        """;

    public const string SecondSeasonEpisodes = """
        {
          "items": [
            { "content_id": "ep_21", "season_number": 2, "episode_number": 1, "title": "Chapter Eleven", "runtime": 50,
              "files": [{ "file_id": "4581", "resolution": "1080p", "codec_video": "h264", "hdr": false, "file_size": 3221225472 }] }
          ]
        }
        """;

    public const string Episode = """
        {
          "content_id": "ep_5", "type": "episode", "title": "Chapter Five", "status": "matched", "genres": [], "keywords": [],
          "series_id": "sr_1", "series_title": "Example Series", "season_number": 1, "episode_number": 5, "runtime": 50,
          "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false },
          "versions": [
            { "file_id": "4575", "resolution": "1080p", "codec_video": "h264", "codec_audio": "eac3", "container": "mkv", "hdr": false,
              "file_size": 2147483648, "duration": 3000, "bitrate": 5000 }
          ],
          "videos": [], "subtitles": []
        }
        """;
}
