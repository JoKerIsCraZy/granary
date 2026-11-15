using static Granary.Tests.Browse.ItemDetailFixture;

namespace Granary.Tests.Browse;

/// <summary>
/// A season's listing of <see cref="ItemDetailPayloads.SeriesId"/> numbered and dated as a test needs, and the episode reads
/// that tell its double episodes, for the places of the episodes the listing lacks.
/// </summary>
internal static class SeasonListing
{
    public static string EpisodeIdOf(int number, int season = 3) => $"episode-tmdb-96677-{season}-{number}";

    public static string Episodes(params (int Number, string AirDate)[] episodes) => Episodes(3, episodes);

    public static string Episodes(int season, params (int Number, string AirDate)[] episodes) =>
        $$"""{ "items": [{{string.Join(",", episodes.Select(e => ListedEpisode(e.Number, e.AirDate, season: season)))}}] }""";

    public static string ListedEpisode(int number, string airDate, string? fileId = null, int season = 3) => $$"""
        { "content_id": "{{EpisodeIdOf(number, season)}}", "season_number": {{season}}, "episode_number": {{number}}, "title": "Chapter {{number}}",
          "air_date": "{{airDate}}", "runtime": 50, "files": [{ "file_id": "{{fileId ?? $"9{season}{number}"}}", "resolution": "1080p", "hdr": false, "file_size": 1 }] }
        """;

    public static string EpisodeItem(int number, int season, int? spansTo)
    {
        string span = spansTo is { } end ? $", \"multi_episode_start\": {number}, \"multi_episode_end\": {end}" : "";
        return $$"""
            {
              "content_id": "{{EpisodeIdOf(number, season)}}", "type": "episode", "title": "Chapter {{number}}", "status": "matched",
              "genres": [], "keywords": [], "series_id": "series-tmdb-96677", "season_number": {{season}}, "episode_number": {{number}},
              "versions": [{ "file_id": "9{{season}}{{number}}", "resolution": "1080p", "codec_video": "h264", "codec_audio": "eac3", "container": "mkv",
                "hdr": false, "file_size": 1, "duration": 3000, "bitrate": 6000{{span}} }],
              "videos": [], "subtitles": []
            }
            """;
    }

    /// <summary>
    /// The read of an episode's files that tells a double episode, made only on a server that lists no double episode's
    /// halves: the episode before a missing place is read.
    /// </summary>
    public static ItemDetailFixture Guard(this ItemDetailFixture fixture, int number, int? spansTo = null, int season = 3) =>
        fixture.Get(ItemPath(EpisodeIdOf(number, season)), Ok(EpisodeItem(number, season, spansTo)));
}
