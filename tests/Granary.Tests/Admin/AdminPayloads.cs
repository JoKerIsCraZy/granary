using Granary.Tests.Browse;

namespace Granary.Tests.Admin;

/// <summary>The admin endpoints' answers, in the server's wire format, for a clock at 2026-10-27 12:30 UTC.</summary>
internal static class AdminPayloads
{
    public static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 30, 0, TimeSpan.Zero);

    public const string Stats = """
        {
          "active_streams": 2, "advisory_titles": 10, "total_files": 12480, "total_items": 900, "total_movie_files": 2048,
          "total_movies": 1990, "total_show_files": 10432, "total_shows": 310, "total_storage_bytes": 111929022480384,
          "total_users": 4, "watch_providers": []
        }
        """;

    /// <summary>An episode that copies its video and converts its audio, and a paused movie that plays directly.</summary>
    public const string Sessions = """
        {
          "items": [
            {
              "session_id": "s1", "user_id": "1", "username": "elena", "profile_id": "p1", "profile_name": "Elena",
              "media_file_id": "77", "media_title": "Secrets", "media_type": "episode", "content_id": "episode:dark-s01e03",
              "series_name": "Dark", "season_number": 1, "episode_number": 3, "episode_name": "Past and Present",
              "poster_url": "/api/v2/artwork/dark.webp?sig=x", "play_method": "remux", "effective_play_method": "direct_stream",
              "is_paused": false, "position_seconds": 600, "file_duration": 3000, "started_at": "2026-10-27T12:25:00.000Z",
              "updated_at": "2026-10-27T12:29:30.000Z", "client_name": "Granary", "client_version": "0.3.0",
              "reporting_node": "local", "stream_location": "local", "audio_track_index": 0, "has_playback_control": true,
              "transcode_audio": true, "source_audio_channels": 6, "source_bitrate_kbps": 8000, "stream_bitrate_kbps": 8000,
              "target_bitrate_kbps": null, "requested_media_file_id": "77"
            },
            {
              "session_id": "s2", "user_id": "2", "username": "kim", "profile_id": "p2", "media_file_id": "12",
              "media_title": "Dune", "media_type": "movie", "play_method": "direct", "is_paused": true, "position_seconds": 10,
              "file_duration": null, "started_at": "2026-10-27T10:00:00.000Z", "updated_at": "2026-10-27T12:29:00.000Z",
              "client_label": "Silo for Android", "reporting_node": "local", "stream_location": "remote", "audio_track_index": 0,
              "has_playback_control": false, "transcode_audio": false, "source_audio_channels": null, "source_bitrate_kbps": null,
              "stream_bitrate_kbps": null, "target_bitrate_kbps": null, "requested_media_file_id": "12"
            }
          ]
        }
        """;

    public const string NoSessions = """{ "items": [] }""";

    public const string Status = """
        {
          "started_at": "2026-10-24T08:30:00.000Z", "restart_required": true, "restart_requested": false, "restart_mark_count": 0,
          "artwork_storage": { "locked": false, "private_locked": false, "status_known": true },
          "health": {
            "errors_24h": 3, "warnings_24h": 1204,
            "postgres": { "configured": true, "ok": true, "latency_ms": 0.0612 },
            "redis": { "configured": false }
          }
        }
        """;

    public const string Build = """{ "available": true, "build_number": 1715, "display": "13ad7a74", "revision": "13ad7a74f", "dirty": false }""";

    /// <summary>One healthy node, one down, one switched off.</summary>
    public const string Nodes = """
        {
          "items": [
            { "id": "n1", "name": "gpu", "type": "transcode", "enabled": true, "healthy": true, "active_jobs": 1, "egress_kbps": 9000 },
            { "id": "n2", "name": "edge", "type": "proxy", "enabled": true, "healthy": false, "active_jobs": 0, "egress_kbps": 0 },
            { "id": "n3", "name": "old", "type": "transcode", "enabled": false, "healthy": false, "active_jobs": 0, "egress_kbps": 0 }
          ]
        }
        """;

    /// <summary>Three minutes sampled at the end of the window, the last a minute ago.</summary>
    public const string Timeseries = """
        {
          "egress_bytes": 48300000000, "download_egress_bytes": 0, "resolution_seconds": 60,
          "from": "2026-10-26T12:30:00.000Z", "to": "2026-10-27T12:30:00.000Z", "oldest_sample_at": null,
          "points": [
            { "t": "2026-10-27T12:27:00.000Z", "streams": 2, "direct": 1, "remux": 1, "transcode": 0, "egress_kbps": 2500, "download_egress_kbps": 0 },
            { "t": "2026-10-27T12:28:00.000Z", "streams": 2, "direct": 1, "remux": 1, "transcode": 0, "egress_kbps": 133000, "download_egress_kbps": 0 },
            { "t": "2026-10-27T12:29:00.000Z", "streams": 2, "direct": 1, "remux": 1, "transcode": 0, "egress_kbps": 12000, "download_egress_kbps": 0 }
          ]
        }
        """;

    public const string PlaybackActivity = """
        {
          "hours": 24, "bucket_seconds": 3600, "profiles_active_24h": 3,
          "from": "2026-10-26T12:30:00.000Z", "to": "2026-10-27T12:30:00.000Z",
          "reliability": { "completed_sessions": 0, "completion_rate": 0, "finalized_sessions": 0, "sessions_started": 0, "transcode_starts": 0, "unique_profiles": 0 },
          "buckets": [
            { "hour": "2026-10-27T10:00:00.000Z", "direct": 1, "remux": 0, "transcode": 0 },
            { "hour": "2026-10-27T12:00:00.000Z", "direct": 0, "remux": 1, "transcode": 1 }
          ]
        }
        """;

    public const string TopActivity = """
        {
          "days": 7, "limit": 10,
          "titles": [
            { "media_item_id": "series:dark", "media_type": "series", "title": "Dark", "plays": 4, "total_seconds": 11160 },
            { "media_item_id": "movie:dune", "media_type": "movie", "title": "", "plays": 1, "total_seconds": 20 }
          ],
          "profiles": [
            { "profile_id": "p1", "profile_name": "Elena", "user_id": "1", "username": "elena", "plays": 4, "total_seconds": 11160 },
            { "profile_id": "p3", "profile_name": "Kids", "user_id": "1", "username": "elena", "plays": 2, "total_seconds": 3000 }
          ]
        }
        """;

    /// <summary>The Owner, an account never active, and a disabled one active an hour ago.</summary>
    public const string Users = """
        {
          "items": [
            { "id": "3", "username": "zoe", "email": "", "role": "user", "enabled": true, "is_owner": false, "created_at": "2026-01-01T00:00:00.000Z", "last_active_at": null },
            { "id": "1", "username": "elena", "email": "elena@example.test", "role": "admin", "enabled": true, "is_owner": true, "created_at": "2025-01-01T00:00:00.000Z", "last_active_at": "2026-10-27T12:29:50.000Z" },
            { "id": "2", "username": "kim", "email": "kim@example.test", "role": "user", "enabled": false, "is_owner": false, "created_at": "2025-06-01T00:00:00.000Z", "last_active_at": "2026-10-27T11:30:00.000Z" }
          ],
          "page": { "has_more": false }
        }
        """;

    public const string Libraries = """
        {
          "items": [
            { "id": "1", "name": "Filme", "type": "movies", "enabled": true, "sort_order": 1, "paths": ["/media/movies"] },
            { "id": "2", "name": "Serien", "type": "series", "enabled": false, "sort_order": 2, "paths": ["/media/tv", "/media/anime"] }
          ]
        }
        """;

    /// <summary>A fake server that answers every dashboard source.</summary>
    public static BrowseFakeSilo Dashboard() => new BrowseFakeSilo()
        .Map("api/v2/admin/stats", Stats)
        .Map("api/v2/admin/sessions", Sessions)
        .Map("api/v2/admin/server/status", Status)
        .Map("api/v2/admin/system/build", Build)
        .Map("api/v2/admin/nodes", Nodes)
        .Map("api/v2/admin/stats/timeseries", Timeseries)
        .Map("api/v2/admin/stats/playback-activity", PlaybackActivity)
        .Map("api/v2/admin/stats/top-activity", TopActivity)
        .Map("api/v2/admin/users", Users)
        .Map("api/v2/libraries", Libraries);
}
