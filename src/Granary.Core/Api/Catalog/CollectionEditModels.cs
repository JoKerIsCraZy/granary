using System.Text.Json;

namespace Granary.Core.Api.Catalog;

/// <summary>The body of <c>POST /api/v2/collections</c>: a new personal collection.</summary>
public sealed record PersonalCollectionCreate
{
    public required string Name { get; init; }

    /// <summary>Sent even when empty, as the web does.</summary>
    public string Description { get; init; } = "";

    /// <summary>Every profile on the login sees it.</summary>
    public bool IsShared { get; init; }

    /// <summary>It shows on the Collections tab of the profiles that see it.</summary>
    public bool IncludeInServerCollections { get; init; }

    /// <summary>"Show only"; left out when it shows everything.</summary>
    public DisplayQuery? DisplayQueryDefinition { get; init; }

    public string CollectionType { get; init; } = PersonalCollectionTypes.Manual;
}

/// <summary>The body of <c>PATCH /api/v2/collections/{id}</c>: every field the editor owns, each time.</summary>
public sealed record PersonalCollectionUpdate
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public bool IsShared { get; init; }

    public bool IncludeInServerCollections { get; init; }

    /// <summary>"Show only"; an empty one (no groups) when it shows everything again.</summary>
    public required DisplayQuery DisplayQueryDefinition { get; init; }
}

/// <summary>A filter-only query fragment: one AND group of rules, as a collection's "Show only" keeps it.</summary>
public sealed record DisplayQuery(string Match, IReadOnlyList<DisplayQueryGroup> Groups);

public sealed record DisplayQueryGroup(string Match, IReadOnlyList<DisplayQueryRule> Rules);

/// <param name="Value">true or false for <c>watched</c>; <c>movie</c> or <c>series</c> for <c>type</c>.</param>
public sealed record DisplayQueryRule(string Field, string Op, JsonElement Value);

/// <summary>
/// A collection's "Show only": the watch state and the kind of title it shows while browsing, as the web's
/// <c>lib/collectionDisplayFilters.ts</c> reads and writes them. Nothing is removed from the collection.
/// </summary>
/// <param name="Watch"><see cref="All"/>, <c>unwatched</c> or <c>watched</c>.</param>
/// <param name="Media"><see cref="All"/>, <c>movie</c> or <c>series</c>.</param>
public sealed record CollectionShowOnly(string Watch, string Media)
{
    public const string All = "all";

    public const string Unwatched = "unwatched";

    public const string Watched = "watched";

    public const string Movies = "movie";

    public const string Shows = "series";

    public static CollectionShowOnly Everything { get; } = new(All, All);

    public bool IsEverything => Watch == All && Media == All;

    /// <summary>Reads a stored fragment; rules it does not know, or none, leave each part at <see cref="All"/>.</summary>
    public static CollectionShowOnly Read(JsonElement definition)
    {
        string watch = All;
        string media = All;
        if (definition.ValueKind != JsonValueKind.Object || !definition.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return Everything;
        }
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object || Text(rule, "op") != "is" || !rule.TryGetProperty("value", out var value))
                {
                    continue;
                }
                switch (Text(rule, "field"))
                {
                    case "watched" when value.ValueKind == JsonValueKind.True:
                        watch = Watched;
                        break;
                    case "watched" when value.ValueKind == JsonValueKind.False:
                        watch = Unwatched;
                        break;
                    case "type" when value.ValueKind == JsonValueKind.String && value.GetString() is Movies or Shows:
                        media = value.GetString()!;
                        break;
                }
            }
        }
        return new CollectionShowOnly(watch, media);
    }

    /// <summary>The fragment to store: one AND group with the rules, or no group at all for everything.</summary>
    public DisplayQuery ToQuery()
    {
        var rules = new List<DisplayQueryRule>();
        if (Watch is Watched or Unwatched)
        {
            rules.Add(new DisplayQueryRule("watched", "is", Json(Watch == Watched ? "true" : "false")));
        }
        if (Media is Movies or Shows)
        {
            rules.Add(new DisplayQueryRule("type", "is", Json($"\"{Media}\"")));
        }
        return new DisplayQuery("all", rules.Count == 0 ? [] : [new DisplayQueryGroup("all", rules)]);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonElement Json(string literal)
    {
        using var document = JsonDocument.Parse(literal);
        return document.RootElement.Clone();
    }
}

/// <summary><c>GET /api/v2/collections/{id}/items</c>: a page of a manual collection's titles, by position.</summary>
public sealed record PersonalCollectionItemPage
{
    public IReadOnlyList<PersonalCollectionItem> Items { get; init => field = value ?? []; } = [];

    public PageInfo? Page { get; init; }
}

public sealed record PersonalCollectionItem
{
    public string MediaItemId { get; init => field = value ?? ""; } = "";

    public long Position { get; init; }

    /// <summary>The catalog title, when the profile can see it.</summary>
    public string? Title { get; init; }
}

/// <summary>The body of <c>PUT /api/v2/collections/{id}/items/{item_id}</c>.</summary>
public sealed record PersonalCollectionItemAdd(long Position);

/// <summary><c>GET</c> and <c>PUT /api/v2/collections/{id}/items/order</c>: the titles' order, with its own version.</summary>
public sealed record PersonalCollectionOrder
{
    public IReadOnlyList<string> OrderedIds { get; init => field = value ?? []; } = [];

    /// <summary>More titles than one order holds (200): the order cannot be changed as a whole.</summary>
    public bool HasMore { get; init; }
}

/// <summary>The body of <c>PUT /api/v2/collections/{id}/items/order</c>: every title, in the new order.</summary>
public sealed record PersonalCollectionOrderChange(IReadOnlyList<string> OrderedIds);

/// <summary><c>GET /api/v2/collections/capabilities</c> (only what the editor needs).</summary>
public sealed record CollectionCapabilities
{
    /// <summary>As sent; read <see cref="TakesArtwork"/>.</summary>
    public bool? Artwork { get; init; }

    /// <summary>A collection takes its own poster; absent counts as yes, as the web reads it.</summary>
    public bool TakesArtwork => Artwork ?? true;

    /// <summary>Without a poster a collection shows a collage of its titles.</summary>
    public bool PosterCollages { get; init; }

    /// <summary>A manual collection's titles can be put in another order.</summary>
    public bool ItemReorder { get; init; }
}
