using System.Text.Json;
using System.Text.Json.Serialization;

namespace Granary.Core.Api.Catalog;

/// <summary>
/// A smart collection's rules (<c>query_definition</c>), as the web's <c>normalizeQueryDefinition</c> keeps them: the
/// libraries (none: every library the profile sees), the kind of titles, how the groups combine, the groups of rules,
/// the order and the most titles. A rule's members this app does not know are kept and sent back as they came.
/// </summary>
public sealed record SmartQuery
{
    /// <summary>The server's stand-in for "no limit"; read as none.</summary>
    public const long NoLimitSentinel = 10_000_000;

    public static IReadOnlyList<string> MediaScopes { get; } = ["movie", "series", "episode", "audiobook", "ebook", "manga", "video"];

    public IReadOnlyList<long> LibraryIds { get; init; } = [];

    /// <summary>Null for every kind of title.</summary>
    public string? MediaScope { get; init; }

    /// <summary>How the groups combine: <c>all</c> or <c>any</c>.</summary>
    public string Match { get; init; } = SmartMatch.All;

    public IReadOnlyList<SmartRuleGroup> Groups { get; init; } = [];

    public SmartSort Sort { get; init; } = new("added_at", "desc");

    /// <summary>The most titles; null for no limit.</summary>
    public long? Limit { get; init; }

    public static SmartQuery Empty { get; } = new();

    /// <summary>
    /// The rules as stored, made whole as the web reads them: unknown kinds and matches fall back, rule fields and the
    /// sort field take their current names, and a limit that means none is dropped.
    /// </summary>
    public static SmartQuery Read(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }
        var groups = new List<SmartRuleGroup>();
        if (raw.TryGetProperty("groups", out var rawGroups) && rawGroups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in rawGroups.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Object))
            {
                var rules = new List<SmartRule>();
                if (group.TryGetProperty("rules", out var rawRules) && rawRules.ValueKind == JsonValueKind.Array)
                {
                    rules.AddRange(rawRules.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).Select(SmartRule.Read));
                }
                groups.Add(new SmartRuleGroup(SmartMatch.Of(Text(group, "match")), rules));
            }
        }
        string? scope = Text(raw, "media_scope");
        string? sortField = null;
        string? sortOrder = null;
        if (raw.TryGetProperty("sort", out var sort) && sort.ValueKind == JsonValueKind.Object)
        {
            sortField = Text(sort, "field");
            sortOrder = Text(sort, "order");
        }
        string field = SmartSort.NormalizeField(sortField) ?? "added_at";
        long? limit = raw.TryGetProperty("limit", out var rawLimit) && rawLimit.TryGetInt64(out long number) ? number : null;
        return new SmartQuery
        {
            LibraryIds = LibraryIdsOf(raw),
            MediaScope = scope is not null && MediaScopes.Contains(scope) ? scope : null,
            Match = SmartMatch.Of(Text(raw, "match")),
            Groups = groups,
            Sort = new SmartSort(field, sortOrder is "asc" or "desc" ? sortOrder : SmartSort.DefaultOrder(field)),
            Limit = limit is > 0 and not NoLimitSentinel ? limit : null,
        };
    }

    /// <summary>The positive whole library ids of a stored definition, once each.</summary>
    public static IReadOnlyList<long> LibraryIdsOf(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("library_ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. ids.EnumerateArray()
            .Select(id => id.ValueKind == JsonValueKind.Number && id.TryGetDouble(out double value) && double.IsFinite(value) && value >= 1 ? (long?)Math.Truncate(value) : null)
            .OfType<long>()
            .Distinct()];
    }

    /// <summary>The same rules for comparing: libraries in id order, and the rules as JSON.</summary>
    public string RulesKey() => JsonSerializer.Serialize(this with { LibraryIds = [] }, CollectionsJsonContext.Default.SmartQuery);

    public string LibrariesKey() => string.Join(",", LibraryIds.Order());

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A group of rules: <c>all</c> of them match, or <c>any</c>.</summary>
public sealed record SmartRuleGroup(string Match, IReadOnlyList<SmartRule> Rules);

/// <summary>
/// One rule: a field, a condition and a value (text, number, true/false, or a two-item range). Other members it
/// carries are kept as they came.
/// </summary>
public sealed record SmartRule
{
    public string Field { get; init; } = "";

    public string Op { get; init; } = "";

    public JsonElement Value { get; init; }

    // Settable, not init: an init-only extension data member counts as a constructor parameter to the source generator.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static SmartRule Read(JsonElement raw)
    {
        Dictionary<string, JsonElement>? extra = null;
        foreach (var member in raw.EnumerateObject().Where(m => m.Name is not ("field" or "op" or "value")))
        {
            (extra ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal))[member.Name] = member.Value.Clone();
        }
        string field = raw.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
        return new SmartRule
        {
            Field = SmartSort.NormalizeField(field) ?? field,
            Op = raw.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.String ? op.GetString() ?? "" : "",
            Value = raw.TryGetProperty("value", out var value) ? value.Clone() : SmartValue.Text(""),
            Extra = extra,
        };
    }
}

/// <param name="Order"><c>asc</c> or <c>desc</c>.</param>
public sealed record SmartSort(string Field, string Order)
{
    // The sort fields the web knows, with the order each starts in.
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["title"] = "asc", ["added_at"] = "desc", ["release_date"] = "desc", ["last_air_date"] = "desc", ["latest_episode_added"] = "desc",
        ["year"] = "desc", ["content_rating"] = "asc", ["runtime"] = "desc", ["rating_imdb"] = "desc", ["rating_tmdb"] = "desc",
        ["rating_rt_critic"] = "desc", ["rating_rt_audience"] = "desc", ["resolution"] = "desc", ["bitrate"] = "desc", ["progress"] = "desc",
        ["date_viewed"] = "desc", ["plays"] = "desc", ["author"] = "asc", ["narrator"] = "asc", ["series"] = "asc",
    };

    /// <summary>A field's current name ("rating" is <c>rating_imdb</c>); null for one that is no sort.</summary>
    public static string? NormalizeField(string? field)
    {
        string? name = field?.Trim().ToLowerInvariant();
        return name switch
        {
            null or "" => null,
            "sort_title" => "title",
            "recently_added" => "added_at",
            "rating" => "rating_imdb",
            "relevance" => "relevance",
            _ => Defaults.ContainsKey(name) ? name : null,
        };
    }

    public static string DefaultOrder(string? field) => NormalizeField(field) is { } name && Defaults.TryGetValue(name, out string? order) ? order : "desc";
}

public static class SmartMatch
{
    public const string All = "all";

    public const string Any = "any";

    public static string Of(string? match) => match == Any ? Any : All;
}

/// <summary>Rule values as the wire carries them.</summary>
public static class SmartValue
{
    public static JsonElement Text(string text) => Parse(JsonSerializer.Serialize(text, CollectionsJsonContext.Default.String));

    public static JsonElement Number(double number) => Parse(number.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    public static JsonElement Bool(bool value) => Parse(value ? "true" : "false");

    /// <summary>A range: each end a number, or text while it is not one yet.</summary>
    public static JsonElement Range(JsonElement from, JsonElement to) => Parse($"[{from.GetRawText()},{to.GetRawText()}]");

    /// <summary>The value as the editor shows it: text, a number without trailing zeros, true or false.</summary>
    public static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// A smart collection's <c>sort_config</c>: kept as stored and sent back with the rules. A <c>field</c> in it is a
/// saved default sort that wins over the rules' order until cleared.
/// </summary>
public static class SmartSortConfig
{
    public static JsonElement Empty => Parse("{}");

    /// <summary>The stored config as an object; anything else counts as none.</summary>
    public static JsonElement Of(JsonElement raw) => raw.ValueKind == JsonValueKind.Object ? raw.Clone() : Empty;

    /// <summary>The saved default sort's field and order, or null without one.</summary>
    public static (string Field, string? Order)? DefaultSort(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String
            || field.GetString()?.Trim() is not { Length: > 0 } name)
        {
            return null;
        }
        string? order = config.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
        return (name, order);
    }

    /// <summary>The config without its default sort: <c>field</c> and <c>order</c> go, any other setting stays.</summary>
    public static JsonElement ClearDefaultSort(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }
        var rest = config.EnumerateObject().Where(m => m.Name is not ("field" or "order")).Select(m => $"{JsonSerializer.Serialize(m.Name, CollectionsJsonContext.Default.String)}:{m.Value.GetRawText()}");
        return Parse("{" + string.Join(",", rest) + "}");
    }

    /// <summary>The config as JSON with its keys in order, so key order never reads as a change.</summary>
    public static string Key(JsonElement config) => config.ValueKind == JsonValueKind.Object
        ? "{" + string.Join(",", config.EnumerateObject().OrderBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => $"{JsonSerializer.Serialize(m.Name, CollectionsJsonContext.Default.String)}:{m.Value.GetRawText()}")) + "}"
        : "{}";

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
