using System.Text.Json;

namespace Granary.Core.Realtime;

/// <summary>The events stream's frame types (docs/api-contract.md, 12); the set is open.</summary>
public static class EventsFrameTypes
{
    public const string Hello = "hello";

    public const string Subscribed = "subscribed";

    public const string Snapshot = "snapshot";

    public const string Event = "event";

    public const string Error = "error";

    /// <summary>The access the ticket was minted under changed; the server closes with 4001 after it.</summary>
    public const string AccessChanged = "access_changed";
}

/// <summary>The channels Granary follows.</summary>
public static class EventsChannels
{
    /// <summary>Titles and libraries added, changed or removed.</summary>
    public const string Catalog = "catalog";

    /// <summary>The profile's own marks: progress, watched, favourite, watchlist, history, Home dismissals.</summary>
    public const string UserState = "user_state";

    /// <summary>The profile's inbox: created and read notifications, and a snapshot of the unread ones on subscribing.</summary>
    public const string Notifications = "notifications";
}

/// <summary>
/// A frame of the events stream. <see cref="Data"/> is the event's or snapshot's payload; <see cref="Rejected"/> names the
/// channels a <c>subscribed</c> frame turned down.
/// </summary>
public sealed record EventsFrame(string Type)
{
    public string? Channel { get; init; }

    /// <summary>The event's name: <c>user_state.changed</c>, <c>catalog.item.changed</c>, <c>notification.created</c>, …</summary>
    public string? Event { get; init; }

    public JsonElement Data { get; init; }

    public IReadOnlyList<string> Rejected { get; init; } = [];

    /// <summary>
    /// On a <c>subscribed</c> frame of a later connection: how long the lane was without one. Changes made meanwhile were
    /// not sent, and the catalog and the profile's state send no snapshot.
    /// </summary>
    public TimeSpan? Gap { get; init; }

    /// <summary>A text of <see cref="Data"/>, null when absent or of another kind.</summary>
    public string? Text(string key) =>
        Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Reads the server's frames and writes the subscription.</summary>
public static class EventsFrames
{
    /// <summary><c>{ "type": "subscribe", "request_id", "channels" }</c>, sent once the server said hello.</summary>
    public static string Subscribe(IReadOnlyList<string> channels, string requestId)
    {
        ArgumentNullException.ThrowIfNull(channels);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "subscribe");
            writer.WriteString("request_id", requestId);
            writer.WriteStartArray("channels");
            foreach (string channel in channels)
            {
                writer.WriteStringValue(channel);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The frame in <paramref name="text"/>; null for what is not one (not JSON, not an object, no type).</summary>
    public static EventsFrame? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            return new EventsFrame(type.GetString()!)
            {
                Channel = Member(root, "channel"),
                Event = Member(root, "event"),
                // The document goes with this call: the payload is kept apart from it.
                Data = root.TryGetProperty("data", out var data) ? data.Clone() : default,
                Rejected = Rejected(root),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The rejected entries are { channel, code }: the channels are what counts.
    private static List<string> Rejected(JsonElement root) =>
        root.TryGetProperty("rejected", out var rejected) && rejected.ValueKind == JsonValueKind.Array
            ? [.. rejected.EnumerateArray().Select(entry => Member(entry, "channel")).OfType<string>()]
            : [];

    private static string? Member(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
