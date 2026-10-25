namespace Granary.Tests.Playback;

/// <summary>
/// Response bodies from the server's contract fixtures (silo-server contracts/api/v2/fixtures, named per
/// constant) and, where the fixtures leave members out, built from the OpenAPI schemas.
/// </summary>
internal static class PlaybackPayloads
{
    public const string InstallationId = "11111111-1111-4111-8111-111111111111";

    public const string SessionId = "11111111-1111-4111-8111-111111111111";

    /// <summary><c>get_watch_state_ok.json</c>.</summary>
    public const string WatchStateOk = """
        {
          "content_id": "movie:heat-1995",
          "type": "movie",
          "title": "Heat",
          "year": 1995,
          "versions": [
            {
              "file_id": "42",
              "resolution": "1080p",
              "codec_video": "h264",
              "codec_audio": "eac3",
              "hdr": false,
              "container": "mkv",
              "file_size": 1024,
              "duration_seconds": 10200,
              "bitrate": 8000000,
              "added_at": "2026-01-02T03:04:05.000Z",
              "video_tracks": [
                {
                  "codec": "h264",
                  "dv_config_present": false,
                  "dv_bl_compat_id_present": false,
                  "width": 1920,
                  "height": 1080,
                  "interlaced": false
                }
              ],
              "audio_tracks": [
                {
                  "language": "eng",
                  "codec": "eac3",
                  "channels": 6,
                  "default": true
                }
              ],
              "chapters": [
                {
                  "index": 1,
                  "title": "Opening",
                  "start_seconds": 0,
                  "end_seconds": 300,
                  "source": "embedded"
                }
              ],
              "intro": {
                "start_seconds": 0,
                "end_seconds": 90
              },
              "marker_segments": [
                {
                  "kind": "intro",
                  "start_seconds": 0,
                  "end_seconds": 90,
                  "thumbnail_url": "https://images.example.test/intro.webp",
                  "thumbnail_thumbhash": "AQID",
                  "thumbnail_capture_seconds": 0
                }
              ],
              "trickplay_available": false
            }
          ],
          "playback_variants": [
            {
              "variant_id": "v1",
              "part_count": 1,
              "default_file_id": "42",
              "parts": [
                {
                  "part_index": 0,
                  "default_file_id": "42",
                  "versions": []
                }
              ]
            }
          ],
          "subtitles": [
            {
              "source": "embedded",
              "language": "eng",
              "forced": false,
              "hearing_impaired": false
            }
          ],
          "credits": {
            "start_seconds": 10000,
            "end_seconds": 10200
          },
          "user_data": {
            "position_seconds": 1325.5,
            "duration_seconds": 10200,
            "is_in_progress": true,
            "watched_count": 0,
            "unplayed_count": 0,
            "in_progress_count": 0,
            "played": false,
            "last_file_id": "3"
          },
          "effective_subtitle_language": "eng",
          "poster_url": "/api/images/poster/heat.jpg?size=medium",
          "poster_thumbhash": "poster-hash",
          "backdrop_url": "/api/images/backdrop/heat.jpg?size=medium",
          "backdrop_thumbhash": "backdrop-hash"
        }
        """;

    /// <summary>
    /// An episode in two parts with every preference field the server can send (schema <c>WatchDetail</c>),
    /// a member the client does not know, and no <c>user_data</c>.
    /// </summary>
    public const string WatchEpisodeTwoParts = """
        {
          "content_id": "episode-tvdb-296762-1-5",
          "type": "episode",
          "title": "The You You Are",
          "overview": "Mark is called into a meeting.",
          "series_id": "series-tvdb-296762",
          "series_title": "Severance",
          "season_number": 1,
          "episode_number": 5,
          "future_member": { "nested": [1, 2] },
          "versions": [
            {
              "file_id": "7",
              "file_name": "Severance S01E05 part1.mkv",
              "resolution": "2160p",
              "codec_video": "hevc",
              "codec_audio": "truehd",
              "hdr": true,
              "container": "mkv",
              "file_size": 40000000000,
              "duration_seconds": 1500,
              "bitrate": 21000,
              "added_at": "2026-02-03T04:05:06.000Z",
              "edition_key": "extended",
              "edition_raw": "Extended Cut",
              "presentation_kind": "multi_part",
              "presentation_group_key": "g1",
              "presentation_part_total": 2,
              "effective_audio_track_index": 1,
              "effective_audio_language": "de",
              "video_tracks": [
                {
                  "codec": "hevc",
                  "dolby_vision": "dvhe.08.06",
                  "dv_profile": 8,
                  "dv_level": 6,
                  "dv_bl_compat_id": 1,
                  "dv_config_present": true,
                  "dv_bl_compat_id_present": true,
                  "dv_bl_present": true,
                  "dv_rpu_present": true,
                  "dv_enhancement_layer": "none",
                  "hdr10_plus": true,
                  "profile": "main 10",
                  "level": 153,
                  "width": 3840,
                  "height": 2160,
                  "interlaced": false,
                  "frame_rate": "24000/1001",
                  "bit_depth": 10,
                  "color_transfer": "smpte2084"
                }
              ],
              "audio_tracks": [
                { "language": "en", "codec": "truehd", "layout": "7.1", "channels": 8, "default": true, "title": "Atmos" },
                { "language": "de", "codec": "eac3", "layout": "5.1(side)", "channels": 6, "default": false }
              ],
              "subtitle_tracks": [
                { "index": 3, "language": "en", "codec": "subrip", "forced": false, "default": true, "hearing_impaired": false, "external": false },
                { "language": "de", "codec": "srt", "forced": true, "default": false, "hearing_impaired": true, "external": true, "file_name": "S01E05.de.forced.srt" }
              ],
              "marker_segments": [
                { "kind": "recap", "start_seconds": 0, "end_seconds": 40 },
                { "kind": "intro", "start_seconds": 60, "end_seconds": 120 },
                { "kind": "intro", "start_seconds": 700, "end_seconds": 720 }
              ],
              "trickplay_available": true
            }
          ],
          "playback_variants": [
            {
              "variant_id": "extended",
              "edition_key": "extended",
              "presentation_kind": "multi_part",
              "part_count": 2,
              "total_duration_seconds": 3000,
              "default_file_id": "7",
              "parts": [
                { "part_index": 0, "default_file_id": "7", "total_duration_seconds": 1500, "versions": [
                  { "file_id": "7", "resolution": "2160p", "codec_video": "hevc", "codec_audio": "truehd", "hdr": true, "container": "mkv",
                    "file_size": 40000000000, "duration_seconds": 1500, "bitrate": 21000, "added_at": "2026-02-03T04:05:06.000Z",
                    "presentation_part_total": 2, "marker_segments": [], "trickplay_available": false }
                ] },
                { "part_index": 1, "default_file_id": "8", "total_duration_seconds": 1500, "versions": [
                  { "file_id": "8", "resolution": "2160p", "codec_video": "hevc", "codec_audio": "truehd", "hdr": true, "container": "mkv",
                    "file_size": 40000000000, "duration_seconds": 1500, "bitrate": 21000, "added_at": "2026-02-03T04:05:06.000Z",
                    "presentation_part_index": 1, "presentation_part_total": 2, "marker_segments": [], "trickplay_available": false }
                ] }
              ]
            }
          ],
          "subtitles": [],
          "effective_subtitle_language": "de",
          "effective_subtitle_mode": "always",
          "effective_show_forced_subtitles": false,
          "effective_subtitle_track_signature": { "source": "external", "language": "de", "codec": "srt", "label": "Deutsch", "forced": true, "hearing_impaired": true },
          "effective_version_resolution": "2160p",
          "effective_version_hdr": true,
          "effective_version_codec_video": "hevc",
          "effective_version_edition_key": "extended",
          "poster_url": "/api/v2/artwork/episode/still.webp?exp=1&sig=s",
          "backdrop_url": "https://cdn.example.test/backdrop.webp"
        }
        """;

    /// <summary>The smallest body the schema allows: only its required members.</summary>
    public const string WatchMinimal = """
        { "content_id": "movie:x", "type": "movie", "title": "X", "versions": [ { "file_id": "1" } ], "subtitles": [] }
        """;

    /// <summary><c>get_watch_state_not_playable.json</c>: a series is not directly playable.</summary>
    public const string WatchStateNotPlayable = """
        {
          "type": "https://siloserver.org/docs/api/v2/problems/validation_failed",
          "title": "Validation failed",
          "status": 422,
          "detail": "The request did not pass validation; see errors.",
          "instance": "urn:silo:request:0000000000000000000000f8",
          "errors": [
            {
              "location": "path.id",
              "code": "invalid",
              "detail": "Content is not directly playable"
            }
          ]
        }
        """;

    /// <summary><c>playback_capability_available.json</c>.</summary>
    public const string CapabilityAvailable = """
        {
          "bandwidth_probe_url": "http://api.example.test/api/v2/playback/bandwidth-probe?media_path=true",
          "bandwidth_probe_target_kbps": 113750,
          "bandwidth_probe_automatic": true,
          "bandwidth_probe_max_age_seconds": 604800,
          "revision": "60ba3ba311d3a4e17db759b1a599a21bd1cfd308dafeffabc1da489d1b895a32",
          "state": "available",
          "allowed": true,
          "installation_id": "11111111-1111-4111-8111-111111111111",
          "protocol_versions": [
            3
          ],
          "features": [
            "playback_plan_v3",
            "neutral_playback_v3_contract_v1",
            "layout_aware_passthrough",
            "device_quirks_v1",
            "output_display_evidence_v1",
            "direct_stream_resume_v1",
            "software_video_decode_v1",
            "plan_source_duration_v1",
            "sequenced_progress_v1",
            "buffering_quality_v1",
            "playback_plan_summary_v1",
            "connection_speed_v1"
          ],
          "deliveries": [
            "original_http",
            "server_transcode_hls"
          ]
        }
        """;

    /// <summary><c>playback_capability_unconfigured.json</c>.</summary>
    public const string CapabilityUnconfigured = """
        {
          "revision": "f6789c729eceb8521a1ddcb20e5e09f1c4f8f7d5ee1365e544e680f589369b90",
          "state": "not_configured",
          "allowed": false,
          "protocol_versions": [],
          "features": [],
          "deliveries": []
        }
        """;

    /// <summary>
    /// <c>playback_start_opaque_ids.json</c>; <c>playback_replan_reanchored.json</c> is the same body, so
    /// <see cref="ReplanReanchored"/> points here.
    /// </summary>
    public const string StartOpaqueIds = """
        {
          "protocol_version": 3,
          "server_features": [
            "playback_plan_v3",
            "neutral_playback_v3_contract_v1",
            "layout_aware_passthrough",
            "device_quirks_v1",
            "output_display_evidence_v1",
            "direct_stream_resume_v1",
            "software_video_decode_v1",
            "plan_source_duration_v1",
            "sequenced_progress_v1"
          ],
          "outcome": "playable",
          "session_id": "11111111-1111-4111-8111-111111111111",
          "playback_plan": {
            "protocol_version": 3,
            "plan_id": "plan:478677870860e5e5108c18bff749b34b",
            "plan_attempt_key": "v3:f0144c47fa349e3e",
            "session_id": "11111111-1111-4111-8111-111111111111",
            "expires_at": "2030-01-01T00:00:00Z",
            "delivery": "original_http",
            "stream": {
              "url": "/api/v2/stream/11111111-1111-4111-8111-111111111111",
              "protocol": "http_progressive",
              "container": "mp4",
              "mime_type": "video/mp4",
              "headers": {},
              "header_refresh": "none"
            },
            "timeline": {
              "source_start_seconds": 12.5,
              "stream_origin_seconds": 0,
              "player_start_seconds": 12.5,
              "timeline_offset_seconds": 0,
              "can_seek_anywhere": true,
              "seek_restoration": "player_position"
            },
            "selected_tracks": {
              "audio": {
                "id": "file:42:audio:0",
                "index": 0
              }
            },
            "effective_recipe": {
              "video_codec": "h264",
              "audio_codec": "aac",
              "width": 1920,
              "height": 1080,
              "frame_rate": 23.976023976023978,
              "bitrate_kbps": 8000,
              "dynamic_range": "sdr",
              "audio_channels": 2,
              "audio_layout": "stereo"
            },
            "claims": {
              "video": {
                "hdr10": false,
                "hdr10_plus": false,
                "hlg": false,
                "dolby_vision": false
              },
              "audio": {
                "codec": "aac",
                "passthrough": false,
                "atmos_preserved": false,
                "reason": "client_decode_supported"
              },
              "subtitles": {
                "ass_styling_preserved": false,
                "bitmap_overlay": false,
                "bitmap_sidecar": false
              }
            },
            "subtitle": {
              "mode": "off",
              "inventory": [
                {
                  "track_id": "file:42:subtitle:0",
                  "combined_index": 0,
                  "source": "external",
                  "codec": "srt",
                  "language": "eng",
                  "label": "English",
                  "forced": false,
                  "default": false,
                  "hearing_impaired": false,
                  "delivery": "sidecar",
                  "url": "/api/v2/stream/11111111-1111-4111-8111-111111111111/subtitles/0.vtt?file_id=42&external_subtitle_key=f903db1c5624c46c4a2c20f2fd8cd247fc0d46e6471c91d4ddb651ae4e039448",
                  "sync_key": "external-f903db1c5624c46c4a2c20f2fd8cd247fc0d46e6471c91d4ddb651ae4e039448"
                },
                {
                  "track_id": "file:42:subtitle:1",
                  "combined_index": 1,
                  "source": "embedded",
                  "codec": "ass",
                  "language": "eng",
                  "label": "English (Signs)",
                  "forced": true,
                  "default": false,
                  "hearing_impaired": false,
                  "delivery": "sidecar",
                  "url": "/api/v2/stream/11111111-1111-4111-8111-111111111111/subtitles/1.ass?file_id=42&embedded_stream_index=0",
                  "font_bundle_url": "/api/v2/stream/11111111-1111-4111-8111-111111111111/subtitles/1/fonts?file_id=42&embedded_stream_index=0"
                },
                {
                  "track_id": "file:42:subtitle:2",
                  "combined_index": 2,
                  "source": "embedded",
                  "codec": "pgs",
                  "language": "jpn",
                  "label": "Japanese",
                  "forced": false,
                  "default": false,
                  "hearing_impaired": false,
                  "delivery": "sidecar",
                  "url": "/api/v2/stream/11111111-1111-4111-8111-111111111111/subtitles/2.sup?file_id=42&embedded_stream_index=1"
                },
                {
                  "track_id": "file:42:subtitle:3",
                  "combined_index": 3,
                  "source": "embedded",
                  "codec": "dvd_subtitle",
                  "language": "fre",
                  "label": "French",
                  "forced": false,
                  "default": false,
                  "hearing_impaired": false,
                  "delivery": "burn_in_only"
                },
                {
                  "track_id": "file:42:subtitle:4",
                  "combined_index": 4,
                  "source": "downloaded",
                  "codec": "srt",
                  "language": "spa",
                  "label": "Spanish (downloaded)",
                  "forced": false,
                  "default": false,
                  "hearing_impaired": false,
                  "delivery": "sidecar",
                  "url": "/api/v2/stream/11111111-1111-4111-8111-111111111111/subtitles/4.vtt?file_id=42"
                }
              ]
            },
            "transformations": [],
            "applied_quirks": [],
            "runtime_corrections": [],
            "available_qualities": [],
            "degradation_warnings": [],
            "decision_reason": "validated_original_playback",
            "requested_media_file_id": "42",
            "effective_media_file_id": "42",
            "source": {
              "media_file_id": "42",
              "duration_seconds": 7200,
              "container": "mp4",
              "video_codec": "h264",
              "video_profile": "high",
              "video_level": 41,
              "bit_depth": 8,
              "width": 1920,
              "height": 1080,
              "frame_rate": 23.976023976023978,
              "bitrate_kbps": 8000,
              "dynamic_range": "sdr",
              "hdr10_plus": false,
              "dv_enhancement_layer": "none",
              "audio_codec": "aac",
              "audio_channels": 2,
              "audio_layout": "stereo"
            },
            "subtitle_fidelity_policy": "allow_simplified_rendering"
          }
        }
        """;

    /// <summary><c>playback_replan_reanchored.json</c> (identical to <c>playback_start_opaque_ids.json</c>).</summary>
    public const string ReplanReanchored = StartOpaqueIds;

    /// <summary>
    /// A transcode of a file whose runtime the server does not know, with a converted sidecar, a quality
    /// menu, server transformations, a learned bandwidth limit and members the client does not know
    /// (schemas <c>PlaybackPlan</c>, <c>TimelineV3</c>, <c>SubtitleDecisionV3</c>).
    /// </summary>
    public const string TranscodeDecision = """
        {
          "protocol_version": 3,
          "server_features": ["playback_plan_v3", "neutral_playback_v3_contract_v1", "plan_active_quality_v1"],
          "outcome": "playable",
          "session_id": "22222222-2222-4222-8222-222222222222",
          "future_member": true,
          "playback_plan": {
            "protocol_version": 3,
            "plan_id": "plan:aa",
            "plan_attempt_key": "v3:bb",
            "delivery": "server_transcode_hls",
            "bandwidth_limit": { "kbps": 6000, "source": "observed_rebuffer", "expires_at": "2026-10-26T00:00:00.000Z" },
            "stream": {
              "url": "/api/v2/playback/transcode/22222222-2222-4222-8222-222222222222/master.m3u8",
              "protocol": "hls",
              "headers": { "X-Test": "1" },
              "header_refresh": "none",
              "future_stream_member": 1
            },
            "timeline": {
              "source_start_seconds": 600,
              "stream_origin_seconds": 598,
              "player_start_seconds": 2,
              "timeline_offset_seconds": 598,
              "seek_window_start_seconds": 598,
              "can_seek_anywhere": false,
              "seek_restoration": "source_position"
            },
            "selected_tracks": { "audio": { "id": "file:9:audio:1" }, "subtitle": { "id": "file:9:subtitle:0", "index": 0 } },
            "effective_recipe": { "video_codec": "h264", "audio_codec": "aac", "width": 1280, "height": 720, "bitrate_kbps": 4000, "dynamic_range": "sdr" },
            "claims": {
              "video": { "hdr10": false, "hdr10_plus": false, "hlg": false, "dolby_vision": false, "dolby_vision_reason": "output_sdr" },
              "audio": { "passthrough": false, "atmos_preserved": false },
              "subtitles": { "ass_styling_preserved": false, "bitmap_overlay": false, "bitmap_sidecar": false, "reason": "converted" }
            },
            "subtitle": {
              "mode": "convert",
              "track_id": "file:9:subtitle:0",
              "artifact": { "url": "/api/v2/stream/22222222-2222-4222-8222-222222222222/subtitles/0.vtt?file_id=9", "mime_type": "text/vtt", "format": "vtt", "timing_origin_seconds": 0 },
              "inventory": [
                { "track_id": "file:9:subtitle:0", "combined_index": 0, "source": "embedded", "codec": "subrip", "language": "en", "forced": false, "default": true,
                  "hearing_impaired": false, "delivery": "sidecar", "url": "/api/v2/stream/22222222-2222-4222-8222-222222222222/subtitles/0.vtt?file_id=9" }
              ]
            },
            "transformations": [
              { "name": "video_to_h264", "executor": "server", "recipe_version": "2", "validated_claims": ["h264_decode"] },
              { "name": "hdr_to_sdr_tonemap", "executor": "server", "recipe_version": "1", "validated_claims": ["hdr_metadata_removed", "sdr_bt709_output"] }
            ],
            "applied_quirks": [],
            "runtime_corrections": [],
            "available_qualities": [
              { "label": "1080p-high", "display_name": "1080p High", "height": 1080, "bitrate_kbps": 12000, "preserves_source": false },
              { "label": "720p-medium", "display_name": "720p Medium", "height": 720, "bitrate_kbps": 4000, "preserves_source": false }
            ],
            "active_quality": "720p-medium",
            "degradation_warnings": [ { "code": "hdr_tone_mapped", "message": "HDR was tone mapped to SDR." } ],
            "decision_reason": "transcode_required",
            "requested_media_file_id": "9",
            "effective_media_file_id": "10",
            "source": { "media_file_id": "10", "container": "mkv", "video_codec": "hevc", "hdr10_plus": false, "dv_enhancement_layer": "none", "dynamic_range": "hdr10" },
            "subtitle_fidelity_policy": "allow_simplified_rendering"
          }
        }
        """;

    /// <summary>A start the server cannot adapt (schema <c>PlaybackDecision</c> with <c>terminal</c>).</summary>
    public const string TerminalDecision = """
        {
          "protocol_version": 3,
          "server_features": ["playback_plan_v3", "neutral_playback_v3_contract_v1"],
          "outcome": "adaptation_unavailable",
          "terminal": { "reason": "route_preparation_failed", "message": "The stream could not be prepared.", "retryable": true }
        }
        """;

    /// <summary><c>playback_installation_changed.json</c>.</summary>
    public const string InstallationChanged = """
        {
          "type": "https://siloserver.org/docs/api/v2/problems/installation_changed",
          "title": "Playback installation changed",
          "status": 409,
          "detail": "Playback installation changed; refresh capabilities",
          "instance": "urn:silo:request:00000000000000000000027f"
        }
        """;

    /// <summary><c>playback_invalid_protocol.json</c>.</summary>
    public const string InvalidProtocol = """
        {
          "type": "https://siloserver.org/docs/api/v2/problems/validation_failed",
          "title": "Validation failed",
          "status": 422,
          "detail": "The request did not pass validation; see errors.",
          "instance": "urn:silo:request:000000000000000000000280",
          "errors": [
            {
              "location": "body",
              "code": "invalid",
              "detail": "protocol_version must be 3"
            }
          ]
        }
        """;

    /// <summary><c>playback_progress_applied.json</c>.</summary>
    public const string ProgressApplied = """
        {
          "outcome": "applied",
          "accepted": {
            "sequence": 42,
            "position": 120,
            "is_paused": false
          }
        }
        """;

    /// <summary><c>playback_progress_stale.json</c>.</summary>
    public const string ProgressStale = """
        {
          "outcome": "stale_sample",
          "accepted": {
            "sequence": 42,
            "position": 120,
            "is_paused": false
          }
        }
        """;

    /// <summary><c>playback_progress_conflict.json</c>.</summary>
    public const string ProgressConflict = """
        {
          "type": "https://siloserver.org/docs/api/v2/problems/progress_conflict",
          "title": "Playback progress conflict",
          "status": 409,
          "detail": "The sequence already has different progress",
          "instance": "urn:silo:request:00000000000000000000027e"
        }
        """;

    /// <summary><c>playback_stop_completed.json</c>.</summary>
    public const string StopCompleted = """
        {
          "outcome": "stopped",
          "accepted": {
            "sequence": 42,
            "position": 120,
            "is_paused": false
          },
          "stop_id": "44444444-4444-4444-8444-444444444444",
          "history_id": "33333333-3333-4333-8333-333333333333"
        }
        """;

    /// <summary><c>playback_stop_replayed.json</c>.</summary>
    public const string StopReplayed = """
        {
          "outcome": "replayed",
          "accepted": {
            "sequence": 42,
            "position": 120,
            "is_paused": false
          },
          "stop_id": "22222222-2222-4222-8222-222222222222",
          "history_id": "33333333-3333-4333-8333-333333333333"
        }
        """;

    /// <summary><c>playback_route_event_accepted.json</c>.</summary>
    public const string RouteEventAccepted = """
        {
          "event_id": "55555555-5555-4555-8555-555555555555",
          "outcome": "accepted"
        }
        """;
}
