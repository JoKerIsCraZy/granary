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

    /// <summary>The playback sessions running now. An administrator's, as the three below.</summary>
    public const string Sessions = "sessions";

    /// <summary>The server's scheduled tasks: <c>task.updated</c> while one runs.</summary>
    public const string Tasks = "tasks";

    /// <summary>Library scans: a snapshot of the active ones on subscribing, then each run as it changes.</summary>
    public const string Scans = "scans";

    /// <summary>The queue that prepares offline downloads.</summary>
    public const string DownloadPreparations = "download_preparations";

    /// <summary>
    /// What the activity indicator follows. The server gives these to the acting administrator only and turns them down
    /// (<c>forbidden</c>) for everyone else, keeping the connection.
    /// </summary>
    public static readonly IReadOnlyList<string> Admin = [Sessions, Tasks, Scans, DownloadPreparations];
}

/// <summary>
/// A frame of the events stream. <see cref="Data"/> is the event's or snapshot's payload; <see cref="Accepted"/> and
/// <see cref="Rejected"/> name the channels a <c>subscribed</c> frame took and turned down, <see cref="Available"/> the
/// ones a <c>hello</c> frame offers.
/// </summary>
public sealed record EventsFrame(string Type)
{
    public string? Channel { get; init; }

    /// <summary>The event's name: <c>user_state.changed</c>, <c>catalog.item.changed</c>, <c>notification.created</c>, …</summary>
    public string? Event { get; init; }

    /// <summary>The payload; empty for a frame that is only a cue (<see cref="EventsFrames.IsCue"/>) or was cut.</summary>
    public JsonElement Data { get; init; }

    /// <summary>
    /// On a <c>hello</c> frame: the channels the server gives this connection (<c>available_channels</c>); null when it
    /// names none.
    /// </summary>
    public IReadOnlyList<string>? Available { get; init; }

    public IReadOnlyList<string> Accepted { get; init; } = [];

    public IReadOnlyList<string> Rejected { get; init; } = [];

    /// <summary>
    /// The lane did not take the frame whole: it was larger than <see cref="EventsLane.MaxFrameBytes"/>, or a long one
    /// that is only a cue. Only what its head names is here, and <see cref="Data"/> is empty. It still says that its
    /// channel has news.
    /// </summary>
    public bool IsCut { get; init; }

    /// <summary>
    /// On a <c>subscribed</c> frame of a later connection: how long the lane was without one (null on its first). Changes
    /// made meanwhile were not sent, and the catalog and the profile's state send no snapshot.
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
    /// <summary>A reading of how far one preparation job is: the one frame of its channel whose payload is used.</summary>
    internal const string PreparationProgress = "download_preparation.progress";

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

    /// <summary>
    /// The frame in <paramref name="text"/>; null for what is not one (not JSON, not an object, no type). A text that ends
    /// before its JSON does is what the socket kept of a frame it did not take whole: it is read by what its head names
    /// (<see cref="EventsFrame.IsCut"/>).
    /// </summary>
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
            string kind = type.GetString()!;
            string? channel = Member(root, "channel");
            string? name = Member(root, "event");
            return new EventsFrame(kind)
            {
                Channel = channel,
                Event = name,
                // The document goes with this call: a payload that is used is kept apart from it.
                Data = !IsCue(kind, channel, name) && root.TryGetProperty("data", out var data) ? data.Clone() : default,
                Accepted = Accepted(root),
                Rejected = Rejected(root),
                Available = Available(root),
            };
        }
        catch (JsonException)
        {
            return Head(System.Text.Encoding.UTF8.GetBytes(text), out string? type, out string? channel, out string? name) && type is not null
                ? new EventsFrame(type) { Channel = channel, Event = name, IsCut = true }
                : null;
        }
    }

    /// <summary>
    /// A frame that only says its list changed: Granary reads the list from the server and never the frame's payload.
    /// Every event and snapshot of <c>sessions</c> and <c>tasks</c>, and of <c>download_preparations</c> all but a
    /// progress reading.
    /// </summary>
    public static bool IsCue(string? type, string? channel, string? name) =>
        type is EventsFrameTypes.Event or EventsFrameTypes.Snapshot && channel switch
        {
            EventsChannels.Sessions or EventsChannels.Tasks => true,
            EventsChannels.DownloadPreparations => name != PreparationProgress,
            _ => false,
        };

    /// <summary>
    /// Whether a frame that starts with <paramref name="head"/> is worth keeping whole: not when its head says it is
    /// only a cue (<see cref="IsCue"/>). An administrator's <c>sessions.replaced</c> carries every running stream,
    /// several hundred kilobytes each time one of them changes. A head that names nothing is kept.
    /// </summary>
    public static bool TakesWhole(ReadOnlySpan<byte> head) =>
        !Head(head, out string? type, out string? channel, out string? name)
        || !IsCue(type, channel, name)
        // An event of the preparations whose name the head does not hold may be the progress, which is read.
        || (type == EventsFrameTypes.Event && channel == EventsChannels.DownloadPreparations && name is null);

    // The server writes type, channel and event before the payload, so the head of a frame names them. Read as a block
    // that is not the last one, the reader stops where the bytes end instead of failing. False for what is wrong before
    // that, and for a whole object, which is read as one.
    private static bool Head(ReadOnlySpan<byte> utf8, out string? type, out string? channel, out string? name)
    {
        type = null;
        channel = null;
        name = null;
        try
        {
            var reader = new Utf8JsonReader(utf8, isFinalBlock: false, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string member = reader.GetString() ?? "";
                if (!reader.Read() || (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip()))
                {
                    break;
                }
                if (reader.TokenType != JsonTokenType.String)
                {
                    continue;
                }
                switch (member)
                {
                    case "type":
                        type = reader.GetString();
                        break;
                    case "channel":
                        channel = reader.GetString();
                        break;
                    case "event":
                        name = reader.GetString();
                        break;
                }
            }
            return reader.TokenType != JsonTokenType.EndObject || reader.CurrentDepth != 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Null when the hello names no channels: an older server, or a connection it gives none.
    private static List<string>? Available(JsonElement root) =>
        root.TryGetProperty("available_channels", out var channels) && channels.ValueKind == JsonValueKind.Array
            ? [.. channels.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString()!)]
            : null;

    private static List<string> Accepted(JsonElement root) =>
        root.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array
            ? [.. channels.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString()!)]
            : [];

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
