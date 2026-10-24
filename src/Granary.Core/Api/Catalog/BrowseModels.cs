namespace Granary.Core.Api.Catalog;

/// <summary><c>GET /api/v2/catalog</c>: one page of a listing (docs/api-contract.md, 7.4).</summary>
public sealed record CatalogBrowseCollection
{
    public IReadOnlyList<CatalogItem> Items { get; init => field = value ?? []; } = [];

    public PageInfo? Page { get; init; }

    /// <summary>Items in the whole result; an estimate unless <see cref="TotalExact"/>.</summary>
    public int Total { get; init; }

    public bool TotalExact { get; init; }

    /// <summary>Opaque seed for explicit window jumps.</summary>
    public string WindowCursor { get; init => field = value ?? ""; } = "";

    /// <summary>The saved or default order a collection or personal list resolved to.</summary>
    public CatalogEffectiveSort? EffectiveSort { get; init; }

    /// <summary>The cursor of the next page, or null on the last page; pass it to <see cref="CatalogQuery.WithCursor"/>.</summary>
    public string? NextCursor => Page is { HasMore: true, NextCursor: { Length: > 0 } next } ? next : null;
}

/// <param name="Order"><c>asc</c> or <c>desc</c>.</param>
public sealed record CatalogEffectiveSort(string Field, string Order);

/// <summary><c>GET /api/v2/catalog/filters</c>: the facet values in a scope, for filter menus.</summary>
public sealed record CatalogFilters
{
    public IReadOnlyList<string> Genres { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> ContentRatings { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> Studios { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> Networks { get; init => field = value ?? []; } = [];

    /// <summary>Normally ISO 3166-1 alpha-2 codes; manual edits can store names.</summary>
    public IReadOnlyList<string> Countries { get; init => field = value ?? []; } = [];

    /// <summary>Normally ISO 639-1 codes.</summary>
    public IReadOnlyList<string> OriginalLanguages { get; init => field = value ?? []; } = [];

    /// <summary>File-derived facets; absent unless asked for.</summary>
    public CatalogTechnicalFilters? Technical { get; init; }
}

public sealed record CatalogTechnicalFilters
{
    public IReadOnlyList<string> Resolutions { get; init => field = value ?? []; } = [];

    /// <summary>Lowercase BCP 47 tags.</summary>
    public IReadOnlyList<string> AudioLanguages { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> SubtitleLanguages { get; init => field = value ?? []; } = [];
}
