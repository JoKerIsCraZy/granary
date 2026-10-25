namespace Granary.Core.Api.Catalog;

/// <summary>
/// The Collections page's lists, for the selected profile. A collection's titles page through
/// <see cref="CatalogApi.BrowseAsync"/> with the <see cref="CatalogSources.UserCollection"/> or
/// <see cref="CatalogSources.LibraryCollection"/> source.
/// </summary>
public sealed class CollectionsApi(SiloHttp http)
{
    /// <summary>The profile's own collections in its order, then the ones other profiles share with it.</summary>
    public Task<PersonalCollectionList> ListAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/collections", SiloAuth.Profile, CollectionsJsonContext.Default.PersonalCollectionList, cancellationToken);

    /// <summary>Every library's server collections, each cut to its first few.</summary>
    public Task<ServerCollectionList> ListServerAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/collections/server", SiloAuth.Profile, CollectionsJsonContext.Default.ServerCollectionList, cancellationToken);
}
