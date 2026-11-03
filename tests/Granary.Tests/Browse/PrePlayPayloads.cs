namespace Granary.Tests.Browse;

/// <summary>
/// A movie with two files, as the tester's example has them: a 2160p HDR10 remux with two audio tracks and three subtitles,
/// and the 1080p BluRay played last. The profile wants English subtitles always.
/// </summary>
internal static class PrePlayPayloads
{
    public const string MovieId = "movie-tmdb-1002";

    public const string Movie = """
        {
          "content_id": "movie-tmdb-1002",
          "type": "movie",
          "title": "Night Harbor",
          "status": "matched",
          "genres": ["Drama"],
          "keywords": [],
          "year": 2025,
          "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false },
          "user_data": { "played": false, "watched_count": 0, "unplayed_count": 1, "in_progress_count": 0, "last_file_id": "502" },
          "effective_subtitle_language": "en",
          "effective_subtitle_mode": "always",
          "versions": [
            {
              "file_id": "502",
              "file_name": "Night.Harbor.2025.1080p.BluRay.x264.DTS.mkv",
              "resolution": "1080p", "codec_video": "h264", "codec_audio": "dts", "container": "mkv", "hdr": false,
              "file_size": 9234179686, "duration": 7200, "bitrate": 10000,
              "effective_audio_track_index": 0,
              "video_tracks": [{ "codec": "h264", "width": 1920, "height": 1080 }],
              "audio_tracks": [{ "language": "eng", "codec": "dts", "channels": 6, "default": true }],
              "subtitle_tracks": [{ "index": 2, "language": "eng", "codec": "subrip", "default": true }]
            },
            {
              "file_id": "501",
              "file_name": "Night.Harbor.2025.2160p.UHD.BluRay.REMUX.HDR10.DTS-HD.MA.7.1.mkv",
              "resolution": "2160p", "codec_video": "hevc", "codec_audio": "dts", "container": "mkv", "hdr": true,
              "file_size": 41768556954, "duration": 7200, "bitrate": 46000,
              "effective_audio_track_index": 0,
              "video_tracks": [{ "codec": "hevc", "width": 3840, "height": 2160, "video_range_type": "HDR10" }],
              "audio_tracks": [
                { "language": "eng", "codec": "dts", "profile": "DTS-HD MA", "layout": "7.1", "channels": 8, "default": true },
                { "language": "ger", "title": "Deutsch", "codec": "ac3", "layout": "5.1(side)", "channels": 6 }
              ],
              "subtitle_tracks": [
                { "index": 4, "language": "ger", "codec": "hdmv_pgs_subtitle", "title": "Forced", "forced": true },
                { "index": 3, "language": "eng", "codec": "subrip", "default": true },
                { "index": 0, "language": "eng", "codec": "srt", "title": "SDH", "external": true, "file_name": "Night.Harbor.2025.en.sdh.srt" }
              ]
            }
          ],
          "videos": [],
          "subtitles": []
        }
        """;

    /// <summary>The movie after a subtitle pick on another device: its English track is remembered.</summary>
    public static string Remembered => Movie.Replace("\"effective_subtitle_mode\": \"always\",",
        "\"effective_subtitle_mode\": \"always\", \"effective_subtitle_track_signature\": { \"source\": \"embedded\", \"language\": \"eng\", \"codec\": \"subrip\", \"forced\": false, \"hearing_impaired\": false },",
        StringComparison.Ordinal);
}
