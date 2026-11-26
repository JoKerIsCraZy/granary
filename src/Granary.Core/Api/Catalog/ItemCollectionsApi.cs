using Granary.Core.Api.Images;

namespace Granary.Core.Api.Catalog;

/// <summary>
/// The collections a movie's or series' page lists (docs/api-contract.md, 7.18a), for the selected profile. Only a server whose
/// search capabilities say <see cref="CatalogSearchCapabilities.ItemCollections"/> has this read.
/// </summary>
public sealed class ItemCollectionsApi(SiloHttp http)
{
    /// <summary>
    /// The collections that hold the title and that the viewer may see, and, when only one does, some of its other titles.
    /// Without <paramref name="libraryId"/> the server takes the first of the title's libraries with box sets on, to leave out
    /// the synced copy of the box set the page shows.
    /// </summary>
    public Task<CatalogItemCollections> ListAsync(Uri server, string itemId, string? libraryId = null, ImageParameters? images = null,
        CancellationToken cancellationToken = default)
    {
        string query = new QueryBuilder().Add("library_id", libraryId).Add(images).ToString();
        return http.GetAsync(server, $"api/v2/catalog/items/{Segment(itemId)}/collections{query}", SiloAuth.Profile,
            ItemCollectionsJsonContext.Default.CatalogItemCollections, cancellationToken);
    }

    // "." and ".." would be removed as dot segments and address another route.
    private static string Segment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is "." or "..")
        {
            throw new ArgumentException($"'{value}' is not an id.", nameof(value));
        }
        return SiloHttp.Escape(value);
    }
}
