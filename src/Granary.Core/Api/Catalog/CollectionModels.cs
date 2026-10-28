using System.Text.Json;

namespace Granary.Core.Api.Catalog;

/// <summary>
/// <c>GET /api/v2/collections</c>: the acting profile's own collections in its order, then the collections other
/// profiles on the login share, grouped by creator.
/// </summary>
public sealed record PersonalCollectionList
{
    public IReadOnlyList<PersonalCollection> Items { get; init => field = value ?? []; } = [];
}

/// <summary>A personal collection (only the members its card and its editor need are modelled).</summary>
public sealed record PersonalCollection
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string Name { get; init => field = value ?? ""; } = "";

    public string Description { get; init => field = value ?? ""; } = "";

    /// <summary><c>manual</c>, <c>smart</c>, or an import source (<c>mdblist</c>, <c>tmdb</c>): a synced list.</summary>
    public string CollectionType { get; init => field = value ?? ""; } = "";

    /// <summary>The profile that made it; only that profile can change it.</summary>
    public string CreatorProfileId { get; init => field = value ?? ""; } = "";

    /// <summary>Every profile on the login sees it, read-only.</summary>
    public bool IsShared { get; init; }

    public int ItemCount { get; init; }

    /// <summary>Presigned and short-lived; empty when there is none.</summary>
    public string? PosterUrl { get; init; }

    /// <summary>A synced list's last sync: <c>running</c>, <c>failed</c>, …; empty until the first.</summary>
    public string LastSyncStatus { get; init => field = value ?? ""; } = "";

    /// <summary>It shows on the Collections tab of the profiles that see it.</summary>
    public bool IncludeInServerCollections { get; init; }

    /// <summary>"Show only" as stored; read it with <see cref="CollectionShowOnly.Read"/>.</summary>
    public JsonElement DisplayQueryDefinition { get; init; }

    /// <summary>Only in a list asked with <c>contains_item</c>, on the profile's own manual collections: the title is in it.</summary>
    public bool? Contains { get; init; }

    public bool IsManual => CollectionType == PersonalCollectionTypes.Manual;
}

/// <summary><c>GET /api/v2/collections/server</c>: the visible server collections, by library.</summary>
public sealed record ServerCollectionList
{
    public IReadOnlyList<ServerCollectionLibrary> Libraries { get; init => field = value ?? []; } = [];
}

/// <summary>
/// A library's server collections, cut to its first few: <see cref="TotalCount"/> says how many there are, and the
/// library's Collections tab (<see cref="LibraryCollectionTab.Collections"/>) lists all of them in the same order.
/// </summary>
public sealed record ServerCollectionLibrary
{
    public string LibraryId { get; init => field = value ?? ""; } = "";

    public string LibraryName { get; init => field = value ?? ""; } = "";

    public int TotalCount { get; init; }

    public IReadOnlyList<LibraryCollectionCard> Collections { get; init => field = value ?? []; } = [];

    public bool IsCutShort => Collections.Count < TotalCount;
}

/// <summary>Values of <see cref="PersonalCollection.CollectionType"/> and <see cref="PersonalCollection.LastSyncStatus"/>.</summary>
public static class PersonalCollectionTypes
{
    public const string Manual = "manual";

    public const string Smart = "smart";

    public const string SyncRunning = "running";

    public const string SyncFailed = "failed";
}
