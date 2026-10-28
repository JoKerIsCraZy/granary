using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Granary.Core.Api.WatchParty;

namespace Granary.Core.WatchParty;

/// <summary>A frame the room socket sent, as far as this client takes it.</summary>
public abstract record WatchPartyFrame
{
    /// <summary>The room as it is now; an older generation of the same room is stale.</summary>
    public sealed record Snapshot(WatchPartyRoom Room) : WatchPartyFrame;

    public sealed record Command(WatchPartyCommand Value) : WatchPartyFrame;

    /// <summary>The answer to a ping, with the server's clock either side of it.</summary>
    public sealed record Pong(DateTimeOffset ClientSentAt, DateTimeOffset ServerReceivedAt, DateTimeOffset ServerSentAt) : WatchPartyFrame;

    /// <summary>The room ended (<c>host_left</c>, <c>room_closed</c>, …), or the server says it is gone.</summary>
    public sealed record Closed(string Reason) : WatchPartyFrame;

    /// <summary>This profile joined the room on another device; this connection is over and does not come back by itself.</summary>
    public sealed record Replaced(string Reason) : WatchPartyFrame;

    /// <summary>The room's suggestions and tallies as they are now; <c>voted_by_me</c> is false in every row.</summary>
    public sealed record Suggestions(IReadOnlyList<WatchPartySuggestion> Items) : WatchPartyFrame;
}

/// <summary>
/// The room socket's frames (silo-server <c>docs/realtime-api.md</c> and the web's
/// <c>useWatchTogetherRoomConnection.ts</c>): JSON objects with a <c>type</c>. Unknown types and malformed frames read
/// as nothing.
/// </summary>
public static class WatchPartyFrames
{
    public const string ReplacedText = "This profile joined the Watch Party on another device.";

    public static WatchPartyFrame? Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") is not { } type)
            {
                return null;
            }
            return type switch
            {
                "snapshot" when root.TryGetProperty("room", out var room) && room.ValueKind == JsonValueKind.Object
                    && room.Deserialize(WatchPartyJsonContext.Default.WatchPartyRoom) is { RoomId.Length: > 0 } snapshot =>
                    new WatchPartyFrame.Snapshot(snapshot),
                "transport_command" when root.TryGetProperty("command", out var command) && Command(command) is { } parsed =>
                    new WatchPartyFrame.Command(parsed),
                "pong" when Instant(root, "client_sent_at") is { } sent && Instant(root, "server_received_at") is { } received
                    && Instant(root, "server_sent_at") is { } answered =>
                    new WatchPartyFrame.Pong(sent, received, answered),
                "suggestions_update" when root.TryGetProperty("suggestions", out var list) && list.ValueKind == JsonValueKind.Array =>
                    new WatchPartyFrame.Suggestions(list.Deserialize(WatchPartyJsonContext.Default.IReadOnlyListWatchPartySuggestion) ?? []),
                "room_closed" => new WatchPartyFrame.Closed(Text(root, "reason") ?? "room_closed"),
                "connection_replaced" => new WatchPartyFrame.Replaced(Text(root, "reason") is { Length: > 0 } reason ? reason : ReplacedText),
                // Some server paths report the end as an error, before or instead of room_closed.
                "error" when Text(root, "code") is "not_found" => new WatchPartyFrame.Closed("not_found"),
                "error" when Text(root, "code") is "gone" => new WatchPartyFrame.Closed("ended"),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Ping(DateTimeOffset now) => Write(w => w.WriteString("client_sent_at", Stamp(now)), "ping");

    /// <summary>Ties this member's playback session to the room; sent on every connection and for every new session.</summary>
    public static string Attach(string sessionId) => Write(w => w.WriteString("session_id", sessionId), "attach_session");

    /// <summary>Where the player is, as a decision: sent every 1.5 s while it plays or holds still on purpose.</summary>
    /// <param name="commandId">The last command applied, so the server takes the report at once.</param>
    /// <param name="isReady">Acknowledges <paramref name="commandId"/> while the room waits, as a <c>ready</c> would.</param>
    public static string StateReport(string sessionId, string? commandId, double positionSeconds, bool isPaused, bool isReady = false) => Write(w =>
    {
        w.WriteString("session_id", sessionId);
        if (commandId is not null)
        {
            w.WriteString("command_id", commandId);
        }
        w.WriteNumber("position_seconds", Position(positionSeconds));
        w.WriteBoolean("is_paused", isPaused);
        if (isReady)
        {
            w.WriteBoolean("is_ready", true);
        }
    }, "state_report");

    /// <summary>The player carried out <paramref name="commandId"/> and can play: the room may go on.</summary>
    public static string Ready(string sessionId, string? commandId, double positionSeconds, bool isPaused) => Write(w =>
    {
        w.WriteString("session_id", sessionId);
        if (commandId is not null)
        {
            w.WriteString("command_id", commandId);
        }
        w.WriteNumber("position_seconds", Position(positionSeconds));
        w.WriteBoolean("is_paused", isPaused);
    }, "ready");

    /// <summary>The player stalled for longer than the grace: the room may wait for it.</summary>
    public static string Buffering(string sessionId, double positionSeconds, bool isPaused) => Write(w =>
    {
        w.WriteString("session_id", sessionId);
        w.WriteNumber("position_seconds", Position(positionSeconds));
        w.WriteBoolean("is_paused", isPaused);
    }, "buffering");

    /// <summary>A member asks the room to play, pause or seek; nothing changes here until the room's command comes.</summary>
    public static string TransportRequest(string action, double positionSeconds, bool isPaused) => Write(w =>
    {
        w.WriteString("action", action);
        w.WriteNumber("position_seconds", Position(positionSeconds));
        w.WriteBoolean("is_paused", isPaused);
    }, "transport_request");

    public static string LobbyReady(bool ready) => Write(w => w.WriteBoolean("ready", ready), "lobby_ready");

    private static WatchPartyCommand? Command(JsonElement command)
    {
        if (command.ValueKind != JsonValueKind.Object || Text(command, "command_id") is not { Length: > 0 } id
            || Text(command, "action") is not ({ } action and (WatchPartyCommand.Play or WatchPartyCommand.Pause or WatchPartyCommand.Seek))
            || Number(command, "position_seconds") is not { } position || position < 0
            || !command.TryGetProperty("selection_revision", out var revision) || !revision.TryGetInt64(out long selection)
            || Instant(command, "execute_at") is not { } executeAt || Text(command, "playback_state") is not { } state)
        {
            return null;
        }
        return new WatchPartyCommand(id, Text(command, "session_id") is { Length: > 0 } session ? session : null, selection, action, position,
            executeAt, state);
    }

    private static string Write(Action<Utf8JsonWriter> body, string type)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // Positions are finite and never negative on the wire.
    private static double Position(double seconds) => double.IsFinite(seconds) ? Math.Max(0, Math.Round(seconds, 3)) : 0;

    private static string Stamp(DateTimeOffset instant) => instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
            && double.IsFinite(number) ? number : null;

    private static DateTimeOffset? Instant(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? instant
            : null;
}
