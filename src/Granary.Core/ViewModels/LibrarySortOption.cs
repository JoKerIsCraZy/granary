using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels;

/// <summary>A sort the library grid offers, with the order it starts in when picked (as on the web).</summary>
/// <param name="Field">A server sort field (<see cref="CatalogSortFields"/>).</param>
public sealed record LibrarySortOption(string Field, string Label, bool StartsDescending)
{
    /// <summary>Title, A to Z: the server's own default order.</summary>
    public static LibrarySortOption Title { get; } = new(CatalogSortFields.Title, "Title", StartsDescending: false);

    public static IReadOnlyList<LibrarySortOption> All { get; } =
    [
        Title,
        new(CatalogSortFields.AddedAt, "Date added", StartsDescending: true),
        new(CatalogSortFields.ReleaseDate, "Release date", StartsDescending: true),
        new(CatalogSortFields.Year, "Year", StartsDescending: true),
        new(CatalogSortFields.RatingImdb, "IMDb rating", StartsDescending: true),
        new(CatalogSortFields.Runtime, "Runtime", StartsDescending: true),
    ];

    public bool IsTitle => Field == CatalogSortFields.Title;
}
