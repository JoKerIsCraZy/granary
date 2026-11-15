using System.Text.Json;

namespace Granary.Core.Texts;

/// <summary>
/// Reads a message as formatjs compiles it into the web's catalogs: an array of elements, each with its <c>type</c>
/// (web/src/locales/en.json). Upstream uses the types 0, 1, 2, 5, 6, 7 and 8, numbers without a style, and plurals only
/// of the cardinal kind.
/// </summary>
internal static class AstReader
{
    /// <exception cref="FormatException">The message is not such an array, or uses something Granary cannot show.</exception>
    public static IReadOnlyList<MessageNode> ReadMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("a message is an array of elements");
        }
        var nodes = new List<MessageNode>(message.GetArrayLength());
        foreach (var element in message.EnumerateArray())
        {
            nodes.Add(ReadNode(element));
        }
        return nodes;
    }

    private static MessageNode ReadNode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException("an element has no type");
        }
        return type.GetInt32() switch
        {
            0 => new LiteralNode(Text(element, "value")),
            1 => new ArgumentNode(Text(element, "value")),
            2 => Number(element),
            // Dates and times (3, 4) are formatted by the caller and passed as text (docs/architecture/i18n.md, "Messages").
            5 => new SelectNode(Text(element, "value"), Options(element)),
            6 => Plural(element),
            7 => PoundNode.Instance,
            8 => new TagNode(Text(element, "value"), ReadMessage(Property(element, "children"))),
            var other => throw new FormatException($"element type {other} is not supported"),
        };
    }

    // A style (percent, a currency, a skeleton) would need formats no upstream message has needed so far.
    private static NumberNode Number(JsonElement element) =>
        element.TryGetProperty("style", out var style) && style.ValueKind != JsonValueKind.Null
            ? throw new FormatException("number styles are not supported")
            : new NumberNode(Text(element, "value"));

    private static PluralNode Plural(JsonElement element)
    {
        // An ordinal would need CLDR's ordinal rules, which no upstream message has needed so far.
        if (element.TryGetProperty("pluralType", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() != "cardinal")
        {
            throw new FormatException($"plural type {kind.GetString()} is not supported");
        }
        int offset = element.TryGetProperty("offset", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
        return new PluralNode(Text(element, "value"), Options(element), offset);
    }

    private static Dictionary<string, IReadOnlyList<MessageNode>> Options(JsonElement element)
    {
        var options = Property(element, "options");
        if (options.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("options are an object");
        }
        var read = new Dictionary<string, IReadOnlyList<MessageNode>>(StringComparer.Ordinal);
        foreach (var option in options.EnumerateObject())
        {
            if (option.Value.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"option {option.Name} is not an object");
            }
            read[option.Name] = ReadMessage(Property(option.Value, "value"));
        }
        if (!read.ContainsKey("other"))
        {
            throw new FormatException("options have no other");
        }
        return read;
    }

    private static string Text(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException($"{name} is not text");
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw new FormatException($"an element has no {name}");
}
