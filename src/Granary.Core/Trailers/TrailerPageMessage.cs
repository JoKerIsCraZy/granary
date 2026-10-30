using System.Text.Json;

namespace Granary.Core.Trailers;

/// <summary>What Granary's trailer page reports of YouTube's player.</summary>
public enum TrailerPageEvent
{
    /// <summary>The player answered: it is up and shows the video.</summary>
    Ready,

    /// <summary>The video started.</summary>
    Playing,

    /// <summary>The player's <c>onError</c>, with its code.</summary>
    Error,

    /// <summary>Esc or Back in the page or the player (<see cref="TrailerPage.CloseKeysScript"/>).</summary>
    Close,
}

/// <summary>
/// A message from the trailer page (<see cref="TrailerPage"/>), in its fixed schema: <c>{"kind":"ready"}</c>,
/// <c>{"kind":"playing"}</c>, <c>{"kind":"error","code":150}</c> or <c>{"kind":"close"}</c>. Anything else, malformed, longer than
/// <see cref="MaximumLength"/>, with another member or a member twice, is not a message.
/// </summary>
public sealed record TrailerPageMessage(TrailerPageEvent Event, int ErrorCode)
{
    /// <summary>The longest message read; the page's own are a fraction of it.</summary>
    public const int MaximumLength = 128;

    private const int MaximumErrorCode = 9999;

    private static readonly JsonDocumentOptions Strict = new() { MaxDepth = 1 };

    /// <summary>The message in <paramref name="json"/> (as the WebView hands it over), or null for anything else.</summary>
    public static TrailerPageMessage? Parse(string? json)
    {
        if (json is not { Length: > 0 and <= MaximumLength })
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json, Strict);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TrailerPageMessage? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string? kind = null;
        int? code = null;
        foreach (var member in root.EnumerateObject())
        {
            switch (member.Name)
            {
                case "kind" when kind is null && member.Value.ValueKind == JsonValueKind.String:
                    kind = member.Value.GetString();
                    break;
                case "code" when code is null && member.Value.ValueKind == JsonValueKind.Number
                    && member.Value.TryGetInt32(out int value) && value is >= 0 and <= MaximumErrorCode:
                    code = value;
                    break;
                default:
                    return null;
            }
        }
        return (kind, code) switch
        {
            ("ready", null) => new TrailerPageMessage(TrailerPageEvent.Ready, 0),
            ("playing", null) => new TrailerPageMessage(TrailerPageEvent.Playing, 0),
            ("close", null) => new TrailerPageMessage(TrailerPageEvent.Close, 0),
            ("error", { } error) => new TrailerPageMessage(TrailerPageEvent.Error, error),
            _ => null,
        };
    }
}
