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
        string? selected = null, string extra = "") => $$"""
        {
          "room_id": "{{RoomId}}", "code": "ABCD1234", "phase": "{{phase}}", "playback_state": "{{state}}",
          "selection_mode": "host_pick", "guest_control_policy": "host_only", "self_role": "{{role}}",
          "selection_revision": {{revision}}, "generation": {{generation}}, "anchor_position_seconds": 0,
          "anchor_updated_at": "2026-10-01T12:00:00.000Z", "is_paused": true, "host_connected": true, "member_count": 2,
          {{(selected is null ? "" : $"\"selected_content_id\": \"{selected}\", \"selected_file_id\": \"77\",")}}
          "self_can_control_transport": true, "self_can_manage_room": true, "self_ignore_wait": false,
          "invite_path": "/rooms?token=invite-secret",
          "members": [
            { "user_id": "1", "profile_id": "7", "display_name": "Elena", "is_host": true, "is_self": {{(role == "host" ? "true" : "false")}}, "connected": true, "lobby_ready": false },
            { "user_id": "2", "profile_id": "9", "display_name": "Maya", "is_host": false, "is_self": {{(role == "host" ? "false" : "true")}}, "connected": true, "lobby_ready": true }
          ]{{extra}}
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
