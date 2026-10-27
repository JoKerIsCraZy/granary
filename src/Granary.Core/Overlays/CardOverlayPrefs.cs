using System.Text.Json;

namespace Granary.Core.Overlays;

/// <summary>The badge styles, as the web's presets.</summary>
public enum OverlayPresetId
{
    Minimal,
    Classic,
    Vibrant,
    Pill,
    Square,
}

/// <summary>One badge's choice: shown or not, in which corner, its colour ("#f5c518") and whether it shows its icon.</summary>
public sealed record OverlayItemConfig(bool Enabled, OverlayPosition Position, string? AccentColor = null, bool? ShowIcon = null);

/// <summary>
/// The profile's badges, as the web stores them in <c>ui.card_overlays</c> (<c>lib/overlays/schema.ts</c>, version 2): a
/// style, an order, and every badge's choice. Reading takes the stored object, the legacy JSON string, a version-1 flat
/// map, or anything malformed, and always ends with every badge set; writing keeps another client's choices for ids the
/// web passes through.
/// </summary>
public sealed record CardOverlayPrefs(OverlayPresetId Preset, IReadOnlyList<string> Order, IReadOnlyDictionary<string, OverlayItemConfig> Items)
{
    /// <summary>Every badge as it starts, in the classic style: what a profile without a choice and a server without defaults shows.</summary>
    public static CardOverlayPrefs Default { get; } = new(OverlayPresetId.Classic, [], BuildItems(null));

    private static readonly OverlayItemConfig PassthroughBase = new(false, OverlayPosition.TopRight);

    public OverlayItemConfig ItemOf(string id) => Items.TryGetValue(id, out var item) ? item
        : OverlayRegistry.Find(id) is { } def ? new(def.DefaultEnabled, def.DefaultPosition) : PassthroughBase;

    public CardOverlayPrefs With(string id, OverlayItemConfig item)
    {
        var items = new Dictionary<string, OverlayItemConfig>(Items, StringComparer.Ordinal) { [id] = item };
        return this with { Items = items };
    }

    /// <summary>
    /// Whether another badge already says what <paramref name="id"/> would: the combined Resolution + HDR badge stands in for
    /// the separate ones, which come back when it goes.
    /// </summary>
    public bool IsSuppressed(string id) => id is "resolution" or "hdr" && ItemOf("resolution_hdr").Enabled;

    /// <summary>The shown badges of a corner, in the profile's order, unranked ones after in the registry's.</summary>
    public IReadOnlyList<OverlayDef> OrderedFor(OverlayPosition position)
    {
        var shown = OverlayRegistry.All.Where(def => ItemOf(def.Id) is { Enabled: true } item && item.Position == position && !IsSuppressed(def.Id));
        if (Order.Count == 0)
        {
            return [.. shown];
        }
        var rank = Order.Select((id, index) => (id, index)).GroupBy(p => p.id).ToDictionary(g => g.Key, g => g.First().index, StringComparer.Ordinal);
        return [.. shown.OrderBy(def => rank.GetValueOrDefault(def.Id, 999))];
    }

    /// <summary>
    /// What a server can store: it refuses the whole document over one id it does not know, so ids newer than its settings
    /// manifest go (or, while its revision is unknown, every id it has not stored itself).
    /// </summary>
    public CardOverlayPrefs ForServer(int? manifestRevision, IReadOnlySet<string> storedIds)
    {
        bool Supported(string id) => IsSupportedBy(id, manifestRevision, storedIds);
        return this with
        {
            Order = [.. Order.Where(Supported)],
            Items = Items.Where(pair => Supported(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>The same choices, whatever holds them: the record itself compares its lists and items by reference.</summary>
    public bool SameAs(CardOverlayPrefs other) =>
        other is not null && (ReferenceEquals(this, other) || ToJson().GetRawText() == other.ToJson().GetRawText());

    /// <summary>The server can store the id: its settings manifest has it, or, while that is unknown, it stored it before.</summary>
    public static bool IsSupportedBy(string id, int? manifestRevision, IReadOnlySet<string> storedIds) =>
        OverlayRegistry.Find(id)?.IntroducedInManifest is not { } since
        || (manifestRevision is { } revision ? revision >= since : storedIds.Contains(id));

    public static CardOverlayPrefs Parse(JsonElement? raw)
    {
        if (raw is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Default;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var document = JsonDocument.Parse(value.GetString() ?? "");
                return Parse(document.RootElement.Clone());
            }
            catch (JsonException)
            {
                return Default;
            }
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return Default;
        }
        if (!LooksLikeVersion2(value))
        {
            // Version 1: a flat map of id to { enabled, position }.
            return new(OverlayPresetId.Classic, [], BuildItems(value));
        }
        var preset = value.TryGetProperty("preset", out var p) && p.ValueKind == JsonValueKind.String && PresetOf(p.GetString()) is { } known
            ? known
            : OverlayPresetId.Classic;
        IReadOnlyList<string> order = value.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Array
            ? [.. o.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(OverlayRegistry.IsKnown)]
            : [];
        var items = value.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Object ? i : (JsonElement?)null;
        return new(preset, order, BuildItems(items));
    }

    /// <summary>A document as JSON text, as the server's defaults come; <see cref="Default"/> for none or anything malformed.</summary>
    public static CardOverlayPrefs ParseText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    /// <summary>The ids a stored value holds, before reading fills in the rest; the server accepted every one of them.</summary>
    public static IReadOnlySet<string> StoredIds(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } value)
        {
            if (raw is { ValueKind: JsonValueKind.String } text)
            {
                try
                {
                    using var document = JsonDocument.Parse(text.GetString() ?? "");
                    return StoredIds(document.RootElement.Clone());
                }
                catch (JsonException)
                {
                    return new HashSet<string>();
                }
            }
            return new HashSet<string>();
        }
        var items = LooksLikeVersion2(value) ? value.TryGetProperty("items", out var i) ? i : default : value;
        return items.ValueKind == JsonValueKind.Object ? items.EnumerateObject().Select(prop => prop.Name).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
    }

    /// <summary>The document as the web writes it: <c>{ "version": 2, "preset", "order", "items" }</c>.</summary>
    public JsonElement ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 2);
            writer.WriteString("preset", PresetName(Preset));
            writer.WriteStartArray("order");
            foreach (string id in Order)
            {
                writer.WriteStringValue(id);
            }
            writer.WriteEndArray();
            writer.WriteStartObject("items");
            foreach (var (id, item) in Items)
            {
                writer.WriteStartObject(id);
                writer.WriteBoolean("enabled", item.Enabled);
                writer.WriteString("position", PositionName(item.Position));
                if (item.AccentColor is { } accent)
                {
                    writer.WriteString("accentColor", accent);
                }
                if (item.ShowIcon is { } showIcon)
                {
                    writer.WriteBoolean("showIcon", showIcon);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    public static string PresetName(OverlayPresetId preset) => preset.ToString().ToLowerInvariant();

    public static string PositionName(OverlayPosition position) => position switch
    {
        OverlayPosition.TopLeft => "top-left",
        OverlayPosition.TopRight => "top-right",
        OverlayPosition.BottomLeft => "bottom-left",
        _ => "bottom-right",
    };

    private static OverlayPresetId? PresetOf(string? name) => name switch
    {
        "minimal" => OverlayPresetId.Minimal,
        "classic" => OverlayPresetId.Classic,
        "vibrant" => OverlayPresetId.Vibrant,
        "pill" => OverlayPresetId.Pill,
        "square" => OverlayPresetId.Square,
        _ => null,
    };

    private static OverlayPosition? PositionOf(string? name) => name switch
    {
        "top-left" => OverlayPosition.TopLeft,
        "top-right" => OverlayPosition.TopRight,
        "bottom-left" => OverlayPosition.BottomLeft,
        "bottom-right" => OverlayPosition.BottomRight,
        _ => null,
    };

    private static bool LooksLikeVersion2(JsonElement value) =>
        (value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int v) && v == 2)
        || (value.TryGetProperty("preset", out var preset) && preset.ValueKind == JsonValueKind.String
            && value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object);

    // Every registry badge, from the stored entry when there is one; the passthrough ids only when stored.
    private static IReadOnlyDictionary<string, OverlayItemConfig> BuildItems(JsonElement? source)
    {
        var items = new Dictionary<string, OverlayItemConfig>(StringComparer.Ordinal);
        foreach (var def in OverlayRegistry.All)
        {
            var item = new OverlayItemConfig(def.DefaultEnabled, def.DefaultPosition);
            items[def.Id] = source is { } s && s.TryGetProperty(def.Id, out var entry) && entry.ValueKind == JsonValueKind.Object ? Patch(item, entry) : item;
        }
        foreach (string id in OverlayRegistry.PassthroughIds)
        {
            if (source is { } s && s.TryGetProperty(id, out var entry) && entry.ValueKind == JsonValueKind.Object)
            {
                items[id] = Patch(PassthroughBase, entry);
            }
        }
        return items;
    }

    private static OverlayItemConfig Patch(OverlayItemConfig item, JsonElement entry) => new(
        entry.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False ? enabled.GetBoolean() : item.Enabled,
        entry.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.String && PositionOf(position.GetString()) is { } p
            ? p
            : item.Position,
        entry.TryGetProperty("accentColor", out var accent) && accent.ValueKind == JsonValueKind.String && IsHex(accent.GetString()) ? accent.GetString() : null,
        entry.TryGetProperty("showIcon", out var icon) && icon.ValueKind is JsonValueKind.True or JsonValueKind.False ? icon.GetBoolean() : null);

    private static bool IsHex(string? value) => value is ['#', .. var digits] && digits.Length == 6 && digits.All(char.IsAsciiHexDigit);
}
