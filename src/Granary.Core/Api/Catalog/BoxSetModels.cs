namespace Granary.Core.Api.Catalog;

/// <summary>
/// A movie box set (docs/api-contract.md, 7.18): the movies TMDB lists in one collection, such as a trilogy, that the
/// viewer can see in one library. Ids are strings on the wire: <see cref="Id"/> is the TMDB collection's, and it opens
/// only together with <see cref="LibraryId"/>. Artwork is presigned like an item's, or TMDB's own URL while the server has
/// not cached it.
/// </summary>
public abstract record CatalogBoxSetBase
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string LibraryId { get; init => field = value ?? ""; } = "";

    /// <summary>The collection's name, a trailing "Collection" written "Box Set" by the server.</summary>
    public string Title { get; init => field = value ?? ""; } = "";

    public string? Overview { get; init; }

    public string? PosterUrl { get; init; }

    public string? PosterThumbhash { get; init; }

    public string? BackdropUrl { get; init; }

    public string? BackdropThumbhash { get; init; }

    /// <summary>The collection's movies in the library that the viewer can see.</summary>
    public int MovieCount { get; init; }

    /// <summary>How many of those the profile has played.</summary>
    public int PlayedCount { get; init; }

    /// <summary>Release year of the earliest of those movies.</summary>
    public int? FirstYear { get; init; }

    public int? LastYear { get; init; }
}

/// <summary>A box set as search lists it, without its movies.</summary>
public sealed record CatalogBoxSet : CatalogBoxSetBase;

/// <summary><c>GET /api/v2/catalog/box-sets/{id}</c>: the box set with its movies the viewer can see, in release order.</summary>
public sealed record CatalogBoxSetDetail : CatalogBoxSetBase
{
    public IReadOnlyList<CatalogItem> Items { get; init => field = value ?? []; } = [];
}

/// <summary><c>GET /api/v2/catalog/items/{id}/box-set</c>: the movie's box set, or none (an empty object).</summary>
public sealed record CatalogItemBoxSet(CatalogBoxSetDetail? BoxSet);

/// <summary><c>GET /api/v2/catalog/box-sets?q=</c>: the box sets matching a search, best match first.</summary>
public sealed record CatalogBoxSetList
{
    public IReadOnlyList<CatalogBoxSet> Items { get; init => field = value ?? []; } = [];
}
