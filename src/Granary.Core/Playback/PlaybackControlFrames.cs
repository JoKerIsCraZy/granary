using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Granary.Core.Playback;

/// <summary>A command the server sent the player on its control socket.</summary>
/// <param name="Name">One of <see cref="PlaybackControlCommands"/>, or a name this player does not take.</param>
/// <param name="IssuedBy"><c>issued_by.kind</c>: <c>admin</c> for an administrator's command, <c>server</c> for the server's own.</param>
public sealed record PlaybackControlCommand(string CommandId, string Name, JsonElement Payload, string? IssuedBy = null)
{
    public bool IsFromAdministrator => string.Equals(IssuedBy?.Trim(), "admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The payload's text under the first of <paramref name="keys"/> it has; null without one. A blank text is none, as
    /// the web reads it: the server sends an admin's message with <c>"title": ""</c> when it has no title.
    /// </summary>
    public string? Text(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (string key in keys)
        {
            if (Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        return null;
    }

    /// <summary>The payload's number under the first of <paramref name="keys"/> it has; a quoted or non-finite number is none.</summary>
    public double? Number(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (string key in keys)
        {
            if (Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out double number) && double.IsFinite(number))
            {
                return number;
            }
        }
        return null;
    }
}

/// <summary>The command names of the control socket (the web's <c>realtime-protocol.ts</c>).</summary>
public static class PlaybackControlCommands
{
    public const string Pause = "pause";
    public const string Unpause = "unpause";
    public const string PlayPause = "play_pause";
    public const string Seek = "seek";
    public const string SetVolume = "set_volume";
    public const string Stop = "stop";
    public const string Terminate = "terminate";
    public const string DisplayMessage = "display_message";
    public const string ServerRestarting = "server_restarting";
    public const string ServerShuttingDown = "server_shutting_down";

    /// <summary>What Granary's player takes, announced in its hello; the server issues it no other command.</summary>
    public static readonly IReadOnlyList<string> Supported =
        [Pause, Unpause, PlayPause, Seek, SetVolume, Stop, Terminate, DisplayMessage, ServerRestarting, ServerShuttingDown];
}

/// <summary>
/// The frames of the control socket (silo-server <c>docs/realtime-api.md</c>; the web's <c>realtime-protocol.ts</c>,
/// Android's <c>PlaybackRealtimeModels.kt</c>): the server sends <c>command</c> and <c>event</c>, the player
/// <c>hello</c> once open, then <c>ack</c> and <c>result</c> for each command.
/// </summary>
public static class PlaybackControlFrames
{
    /// <summary>
    /// The command a frame carries for <paramref name="sessionId"/>; null for an event, another session's frame, or
    /// anything malformed. Never throws.
    /// </summary>
    public static PlaybackControlCommand? ParseCommand(string text, string sessionId)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || String(root, "type") != "command" || String(root, "session_id") != sessionId
                || String(root, "command_id") is not { Length: > 0 } commandId || String(root, "name") is not { Length: > 0 } name)
            {
                return null;
            }
            var payload = root.TryGetProperty("payload", out var value) && value.ValueKind == JsonValueKind.Object ? value.Clone() : default;
            string? issuedBy = root.TryGetProperty("issued_by", out var issuer) && issuer.ValueKind == JsonValueKind.Object ? String(issuer, "kind") : null;
            return new PlaybackControlCommand(commandId, name, payload, issuedBy);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What the player says once the socket opened: its name and the commands it takes.</summary>
    public static string Hello(string sessionId) => Write(writer =>
    {
        writer.WriteString("type", "hello");
        writer.WriteString("session_id", sessionId);
        writer.WriteStartObject("client");
        writer.WriteString("name", "granary");
        writer.WriteString("version", "1");
        writer.WriteEndObject();
        writer.WriteStartObject("capabilities");
        writer.WriteStartArray("commands");
        foreach (string command in PlaybackControlCommands.Supported)
        {
            writer.WriteStringValue(command);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    /// <summary>The command arrived.</summary>
    public static string Ack(string sessionId, string commandId) => Write(writer =>
    {
        writer.WriteString("type", "ack");
        writer.WriteString("command_id", commandId);
        writer.WriteString("session_id", sessionId);
        writer.WriteString("status", "accepted");
    });

    /// <summary>The command was carried out (<c>completed</c>) or not (<c>rejected</c>, with why).</summary>
    public static string Result(string sessionId, string commandId, bool completed, string? error = null) => Write(writer =>
    {
        writer.WriteString("type", "result");
        writer.WriteString("command_id", commandId);
        writer.WriteString("session_id", sessionId);
        writer.WriteString("status", completed ? "completed" : "rejected");
        if (!completed && !string.IsNullOrEmpty(error))
        {
            writer.WriteString("error", error);
        }
    });

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
