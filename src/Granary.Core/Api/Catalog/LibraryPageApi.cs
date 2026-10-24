namespace Granary.Core.Api.Catalog;

/// <summary>
/// The library page's calls that <see cref="CatalogApi"/> does not make: the Collections tab and Shuffle.
/// Both act for the selected profile. The titles of a collection page through
/// <see cref="CatalogApi.BrowseAsync"/> with the <see cref="CatalogSources.LibraryCollection"/> source.
/// </summary>
public sealed class LibraryPageApi(SiloHttp http)
{
    /// <summary>The scope kind of a whole library.</summary>
    public const string LibraryScope = "library";

    /// <summary>The library's curated collections, their groups and the viewer's opted-in personal collections.</summary>
    public Task<LibraryCollectionTab> GetCollectionsAsync(Uri server, string libraryId, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, $"api/v2/library/{Segment(libraryId)}/collections", SiloAuth.Profile,
            LibraryPageJsonContext.Default.LibraryCollectionTab, cancellationToken);

    public Task<LibraryShuffleCapability> GetShuffleCapabilityAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/shuffles/capabilities", SiloAuth.Profile, LibraryPageJsonContext.Default.LibraryShuffleCapability, cancellationToken);

    /// <summary>
    /// Starts a shuffle of the library's movies and episodes and answers its first random pick. Non-retryable
    /// (a retry starts another shuffle; the server deletes abandoned ones). 409 when nothing in it can play.
    /// </summary>
    public Task<LibraryShuffle> StartLibraryShuffleAsync(Uri server, string libraryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        return http.PostAsync(server, "api/v2/shuffles", SiloAuth.Profile, new LibraryShuffleRequest(new LibraryShuffleScope(LibraryScope, libraryId)),
            LibraryPageJsonContext.Default.LibraryShuffleRequest, LibraryPageJsonContext.Default.LibraryShuffle, cancellationToken);
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
