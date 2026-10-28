namespace Granary.Tests.WatchParty;

/// <summary>Room answers as a Silo server sends them; the room proof and ticket are made up.</summary>
internal static class WatchPartyPayloads
{
    public const string RoomId = "6f1c2c1e-7f43-4b8e-9c55-1d2a3b4c5d6e";
    public const string Token = "room-proof-1";
    public const string Ticket = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE";
    public const string Session = "sess-1";

    public static string TicketAnswer => $$"""{ "ticket": "{{Ticket}}", "expires_in": 30, "max_connection_seconds": 300, "protocol": "silo.room.v2" }""";

    /// <summary>A room snapshot; <paramref name="extra"/> adds members after the generation.</summary>
    public static string Room(string phase = "lobby", string state = "idle", long generation = 1, long revision = 0, string role = "host",
        string? selected = null, string extra = "", string mode = "host_pick", string policy = "host_only", bool mayaReady = true) => $$"""
        {
          "room_id": "{{RoomId}}", "code": "ABCD1234", "phase": "{{phase}}", "playback_state": "{{state}}",
          "selection_mode": "{{mode}}", "guest_control_policy": "{{policy}}", "self_role": "{{role}}",
          "selection_revision": {{revision}}, "generation": {{generation}}, "anchor_position_seconds": 0,
          "anchor_updated_at": "2026-10-01T12:00:00.000Z", "is_paused": true, "host_connected": true, "member_count": 2,
          {{(selected is null ? "" : $"\"selected_content_id\": \"{selected}\", \"selected_file_id\": \"77\",")}}
          "self_can_control_transport": true, "self_can_manage_room": true, "self_ignore_wait": false,
          "invite_path": "/rooms/join?token=invite-secret",
          "members": [
            { "user_id": "1", "profile_id": "7", "display_name": "Elena", "is_host": true, "is_self": {{(role == "host" ? "true" : "false")}}, "connected": true, "lobby_ready": false },
            { "user_id": "2", "profile_id": "9", "display_name": "Maya", "is_host": false, "is_self": {{(role == "host" ? "false" : "true")}}, "connected": true, "lobby_ready": {{(mayaReady ? "true" : "false")}} }
          ]{{extra}}
        }
        """;

    /// <summary>A suggestion row as the HTTP list sends it (string ids); Maya (2:9) suggested it unless said otherwise.</summary>
    public static string Suggestion(string id, string title, int votes = 0, bool mine = false, string user = "2", string profile = "9",
        string content = "movie:heat", string created = "2026-10-01T12:00:00Z", string subtitle = "1995") => $$"""
        { "id": "{{id}}", "room_id": "{{RoomId}}", "suggester_user_id": "{{user}}", "suggester_profile_id": "{{profile}}",
          "content_id": "{{content}}", "content_type": "movie", "title": "{{title}}", "subtitle": "{{subtitle}}",
          "poster_url": "/api/v2/images/{{content}}/poster", "note": "", "vote_count": {{votes}}, "voted_by_me": {{(mine ? "true" : "false")}},
          "created_at": "{{created}}" }
        """;

    public static string Suggestions(params string[] rows) => $$"""{ "items": [{{string.Join(",", rows)}}], "page": { "has_more": false } }""";

    /// <summary>The socket's suggestions_update: numeric user ids, nobody's own vote.</summary>
    public static string SuggestionsUpdate(params (string Id, string Title, int Votes)[] rows) => $$"""
        { "type": "suggestions_update", "suggestions": [{{string.Join(",", rows.Select(r => $$"""
          { "id": "{{r.Id}}", "room_id": "{{RoomId}}", "suggester_user_id": 2, "suggester_profile_id": "9", "content_id": "movie:{{r.Id}}",
            "content_type": "movie", "title": "{{r.Title}}", "subtitle": "", "poster_url": "", "note": "", "vote_count": {{r.Votes}},
            "voted_by_me": false, "created_at": "2026-10-01T12:00:00Z" }
          """))}}] }
        """;

    /// <summary>The picker: Heat on Maya's and Elena's watchlists, a series both are part way through.</summary>
    public static string Picker => """
        {
          "members": [],
          "continue_together": [
            { "item": { "content_id": "series:dark", "type": "series", "title": "Dark", "year": 2017, "poster_url": "/img/dark.jpg" },
              "members": [
                { "user_id": "1", "profile_id": "7", "display_name": "Elena", "position_seconds": 1200, "duration_seconds": 3000 },
                { "user_id": "2", "profile_id": "9", "display_name": "Maya", "position_seconds": 600, "duration_seconds": 3000 }
              ],
              "next_up": { "content_id": "episode:dark-2-3", "season_number": 2, "episode_number": 3, "title": "Ghosts", "member_count": 2 } }
          ],
          "watchlist_union": [
            { "item": { "content_id": "movie:heat", "type": "movie", "title": "Heat", "year": 1995, "poster_url": "/img/heat.jpg" },
              "members": [
                { "user_id": "2", "profile_id": "9", "display_name": "Maya" },
                { "user_id": "1", "profile_id": "7", "display_name": "Elena" },
                { "user_id": "3", "profile_id": "4", "display_name": "Noor" }
              ] }
          ]
        }
        """;

    public static string Answer(string room) => $$"""{ "room": {{room}}, "room_access_token": "{{Token}}" }""";

    public static string Snapshot(string room) => $$"""{ "type": "snapshot", "room": {{room}} }""";

    public static string Command(string id, string action, double position, string executeAt, string state = "playing", long revision = 1,
        string? session = null) => $$"""
        { "type": "transport_command", "command": { "command_id": "{{id}}", {{(session is null ? "" : $"\"session_id\": \"{session}\",")}}
          "selection_revision": {{revision}}, "action": "{{action}}", "position_seconds": {{position.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          "execute_at": "{{executeAt}}", "issued_at": "{{executeAt}}", "playback_state": "{{state}}" } }
        """;
}
