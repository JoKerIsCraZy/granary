namespace Granary.Core.Api.Catalog;

/// <summary>
/// <c>GET /api/v2/catalog/items/{id}/collections</c> (docs/api-contract.md, 7.18a): the collections that hold a movie or series
/// and that the viewer may see, server collections first, then personal ones, each group by title.
/// </summary>
public sealed record CatalogItemCollections
{
    public IReadOnlyList<SectionCollectionCard> Items { get; init => field = value ?? []; } = [];

    /// <summary>When <see cref="Items"/> holds exactly one collection, up to six of its other titles in its order; else empty.</summary>
    public IReadOnlyList<CatalogItem> PreviewItems { get; init => field = value ?? []; } = [];
}

/// <summary>A collection's card as a row of collections carries it: a server collection or a personal one.</summary>
public sealed record SectionCollectionCard
{
    public string Id { get; init => field = value ?? ""; } = "";

    /// <summary><see cref="SectionCollectionSources"/>.</summary>
    public string Source { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";

    /// <summary>Presigned and short-lived; empty when there is none.</summary>
    public string? PosterUrl { get; init; }

    public string? PosterThumbhash { get; init; }

    /// <summary>The titles the acting profile sees in it.</summary>
    public int ItemCount { get; init; }

    /// <summary>On a server collection: a library the viewer can open that holds it, which its page opens in.</summary>
    public string? LibraryId { get; init; }

    /// <summary>On a personal collection: the profile that made it.</summary>
    public string? CreatorProfileId { get; init; }

    public bool IsPersonal => Source == SectionCollectionSources.User;
}

/// <summary>Values of <see cref="SectionCollectionCard.Source"/>.</summary>
public static class SectionCollectionSources
{
    /// <summary>A server collection.</summary>
    public const string Library = "library";

    /// <summary>A personal collection.</summary>
    public const string User = "user";
}
