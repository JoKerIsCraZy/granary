namespace Granary.Core.Api.Catalog;

/// <summary>
/// <c>GET /api/v2/library/{id}/collections</c>: the library's Collections tab. Show <see cref="Groups"/>
/// and <see cref="Ungrouped"/> by their sort order; a server without collection groups sends neither and
/// lists the curated collections only in <see cref="Collections"/>.
/// </summary>
public sealed record LibraryCollectionTab
{
    public string LibraryId { get; init => field = value ?? ""; } = "";

    /// <summary>Every visible curated collection in full (only the members a card needs are modelled).</summary>
    public IReadOnlyList<LibraryCuratedCollection> Collections { get; init => field = value ?? []; } = [];

    /// <summary>Non-empty groups in display order.</summary>
    public IReadOnlyList<LibraryCollectionGroup> Groups { get; init => field = value ?? []; } = [];

    /// <summary>Absent when every collection is grouped.</summary>
    public LibraryCollectionUngrouped? Ungrouped { get; init; }
}

/// <summary>A named shelf of collections.</summary>
public sealed record LibraryCollectionGroup
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string Name { get; init => field = value ?? ""; } = "";

    /// <summary><see cref="LibraryCollectionGroupKinds"/>.</summary>
    public string Kind { get; init => field = value ?? ""; } = "";

    public int SortOrder { get; init; }

    public IReadOnlyList<LibraryCollectionCard> Collections { get; init => field = value ?? []; } = [];
}

/// <summary>The collections outside every group, placed among the groups by <see cref="SortOrder"/>.</summary>
public sealed record LibraryCollectionUngrouped
{
    public int SortOrder { get; init; }

    public IReadOnlyList<LibraryCollectionCard> Collections { get; init => field = value ?? []; } = [];
}

public sealed record LibraryCollectionCard
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";

    /// <summary>Presigned and short-lived; empty when there is none.</summary>
    public string? PosterUrl { get; init; }

    public int ItemCount { get; init; }

    /// <summary>Present on a personal collection: the profile that made it.</summary>
    public string? CreatorProfileId { get; init; }
}

/// <summary>A curated collection as <see cref="LibraryCollectionTab.Collections"/> lists it.</summary>
public sealed record LibraryCuratedCollection
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";

    public string? PosterUrl { get; init; }

    public int ItemCount { get; init; }

    public int SortOrder { get; init; }
}

/// <summary>Values of <see cref="LibraryCollectionGroup.Kind"/>.</summary>
public static class LibraryCollectionGroupKinds
{
    public const string Admin = "admin";

    /// <summary>The viewer's opted-in personal collections; they page as <see cref="CatalogSources.UserCollection"/>.</summary>
    public const string UserCollections = "user_collections";
}

/// <summary><c>GET /api/v2/shuffles/capabilities</c>: show shuffle actions only while <see cref="CanShuffle"/> holds for the scope.</summary>
public sealed record LibraryShuffleCapability
{
    public string State { get; init => field = value ?? ""; } = "";

    public bool Allowed { get; init; }

    /// <summary>Scope kinds a shuffle can draw from (library, series, season, library_collection, user_collection).</summary>
    public IReadOnlyList<string> ScopeKinds { get; init => field = value ?? []; } = [];

    public bool CanShuffle(string scopeKind) => State == "available" && Allowed && ScopeKinds.Contains(scopeKind, StringComparer.Ordinal);
}

/// <summary>The body of <c>POST /api/v2/shuffles</c>.</summary>
public sealed record LibraryShuffleRequest(LibraryShuffleScope Scope);

/// <param name="Kind"><c>library</c>, <c>series</c>, <c>season</c>, <c>library_collection</c> or <c>user_collection</c>.</param>
public sealed record LibraryShuffleScope(string Kind, string Id);

/// <summary>A started shuffle; only what the library page uses is modelled.</summary>
public sealed record LibraryShuffle
{
    public string Id { get; init => field = value ?? ""; } = "";

    /// <summary>The random pick to play now: a movie or an episode.</summary>
    public LibraryShufflePick? Current { get; init; }
}

public sealed record LibraryShufflePick
{
    public string ContentId { get; init => field = value ?? ""; } = "";

    public string Type { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";
}
