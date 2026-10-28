namespace Granary.Core.Api.Catalog;

/// <summary>
/// The Collections page's lists, and the profile's own collections made and changed (docs/api-contract.md, 7.17), for
/// the selected profile. A collection's titles page through <see cref="CatalogApi.BrowseAsync"/> with the
/// <see cref="CatalogSources.UserCollection"/> or <see cref="CatalogSources.LibraryCollection"/> source. A collection and
/// its titles' order are each written at the version they were read at: one changed since answers 412.
/// </summary>
public sealed class CollectionsApi(SiloHttp http)
{
    /// <summary>Titles one page of a collection's items holds, the most the server gives.</summary>
    public const int ItemPageSize = 200;

    /// <summary>The profile's own collections in its order, then the ones other profiles share with it.</summary>
    public Task<PersonalCollectionList> ListAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/collections", SiloAuth.Profile, CollectionsJsonContext.Default.PersonalCollectionList, cancellationToken);

    /// <summary>As <see cref="ListAsync"/>; the profile's own manual collections say whether <paramref name="contentId"/> is in them.</summary>
    public Task<PersonalCollectionList> ListContainingAsync(Uri server, string contentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        return http.GetAsync(server, "api/v2/collections" + new QueryBuilder().Add("contains_item", contentId).ToString(), SiloAuth.Profile,
            CollectionsJsonContext.Default.PersonalCollectionList, cancellationToken);
    }

    public Task<CollectionCapabilities> GetCapabilitiesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/collections/capabilities", SiloAuth.Profile, CollectionsJsonContext.Default.CollectionCapabilities, cancellationToken);

    /// <summary>One collection, at the version a change sends back. It carries no poster: the list does.</summary>
    public Task<Versioned<PersonalCollection>> GetAsync(Uri server, string id, CancellationToken cancellationToken = default) =>
        http.GetVersionedAsync(server, Path(id), SiloAuth.Profile, CollectionsJsonContext.Default.PersonalCollection, cancellationToken);

    /// <summary>Makes a collection; not repeatable, a second call makes a second one.</summary>
    public Task<PersonalCollection> CreateAsync(Uri server, PersonalCollectionCreate collection, CancellationToken cancellationToken = default) =>
        http.PostAsync(server, "api/v2/collections", SiloAuth.Profile, collection, CollectionsJsonContext.Default.PersonalCollectionCreate,
            CollectionsJsonContext.Default.PersonalCollection, cancellationToken);

    /// <summary>Changes the collection as read at <paramref name="version"/>.</summary>
    public Task<Versioned<PersonalCollection>> UpdateAsync(Uri server, string id, string version, PersonalCollectionUpdate change,
        CancellationToken cancellationToken = default) =>
        http.SendIfMatchAsync(HttpMethod.Patch, server, Path(id), SiloAuth.Profile, version, change, CollectionsJsonContext.Default.PersonalCollectionUpdate,
            CollectionsJsonContext.Default.PersonalCollection, cancellationToken);

    public Task DeleteAsync(Uri server, string id, string version, CancellationToken cancellationToken = default) =>
        http.SendIfMatchAsync(HttpMethod.Delete, server, Path(id), SiloAuth.Profile, version, cancellationToken);

    /// <summary>A page of a manual collection's titles, by position.</summary>
    public Task<PersonalCollectionItemPage> ListItemsAsync(Uri server, string id, string? cursor = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, Path(id) + "/items" + new QueryBuilder().Add("limit", ItemPageSize).Add("cursor", cursor).ToString(), SiloAuth.Profile,
            CollectionsJsonContext.Default.PersonalCollectionItemPage, cancellationToken);

    /// <summary>Puts a title in a manual collection at <paramref name="position"/>; one already in it stays.</summary>
    public Task AddItemAsync(Uri server, string id, string itemId, long position, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Put, server, ItemPath(id, itemId), SiloAuth.Profile, new PersonalCollectionItemAdd(position),
            CollectionsJsonContext.Default.PersonalCollectionItemAdd, cancellationToken);

    public Task RemoveItemAsync(Uri server, string id, string itemId, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Delete, server, ItemPath(id, itemId), SiloAuth.Profile, cancellationToken);

    /// <summary>The titles' order, at its own version.</summary>
    public Task<Versioned<PersonalCollectionOrder>> GetOrderAsync(Uri server, string id, CancellationToken cancellationToken = default) =>
        http.GetVersionedAsync(server, Path(id) + "/items/order", SiloAuth.Profile, CollectionsJsonContext.Default.PersonalCollectionOrder, cancellationToken);

    /// <summary>Titles a preview of rules shows, as the web asks.</summary>
    public const int PreviewLimit = 24;

    /// <summary>The first titles <paramref name="rules"/> match across the profile's libraries, and how many do.</summary>
    public Task<CollectionPreview> PreviewAsync(Uri server, SmartQuery rules, int limit = PreviewLimit, CancellationToken cancellationToken = default) =>
        http.PostAsync(server, "api/v2/collections/preview", SiloAuth.Profile, new CollectionPreviewRequest(rules, limit),
            CollectionsJsonContext.Default.CollectionPreviewRequest, CollectionsJsonContext.Default.CollectionPreview, cancellationToken);

    /// <summary>Puts every title in <paramref name="orderedIds"/>'s order, as the order was read at <paramref name="version"/>.</summary>
    public Task<Versioned<PersonalCollectionOrder>> SetOrderAsync(Uri server, string id, string version, IReadOnlyList<string> orderedIds,
        CancellationToken cancellationToken = default) =>
        http.SendIfMatchAsync(HttpMethod.Put, server, Path(id) + "/items/order", SiloAuth.Profile, version, new PersonalCollectionOrderChange(orderedIds),
            CollectionsJsonContext.Default.PersonalCollectionOrderChange, CollectionsJsonContext.Default.PersonalCollectionOrder, cancellationToken);

    /// <summary>Every library's server collections, each cut to its first few.</summary>
    public Task<ServerCollectionList> ListServerAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/collections/server", SiloAuth.Profile, CollectionsJsonContext.Default.ServerCollectionList, cancellationToken);

    private static string Path(string id) => "api/v2/collections/" + SiloHttp.PathSegment(id, nameof(id));

    private static string ItemPath(string id, string itemId) => Path(id) + "/items/" + SiloHttp.PathSegment(itemId, nameof(itemId));
}
