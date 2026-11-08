using Granary.Core.Api.Images;

namespace Granary.Core.Api.Catalog;

/// <summary>
/// Movie box sets (docs/api-contract.md, 7.18), for the selected profile: a movie's own, one by its id, and those a search
/// finds. A server without box sets answers 404; so does a box set whose library has them off or whose movies the viewer
/// sees too few of.
/// </summary>
public sealed class BoxSetsApi(SiloHttp http)
{
    /// <summary>The box sets a search lists, as the web asks for (<c>hooks/queries/boxSets.ts</c>).</summary>
    public const int SearchLimit = 5;

    /// <summary>
    /// The box set a movie's page shows, or null when the movie is in none the viewer sees (also for a series). Without
    /// <paramref name="libraryId"/> the server takes the first of the movie's libraries with box sets on.
    /// </summary>
    public async Task<CatalogBoxSetDetail?> GetForItemAsync(Uri server, string itemId, string? libraryId = null, ImageParameters? images = null,
        CancellationToken cancellationToken = default)
    {
        string query = new QueryBuilder().Add("library_id", libraryId).Add(images).ToString();
        var answer = await http.GetAsync(server, $"api/v2/catalog/items/{Segment(itemId)}/box-set{query}", SiloAuth.Profile,
            BoxSetsJsonContext.Default.CatalogItemBoxSet, cancellationToken).ConfigureAwait(false);
        return answer.BoxSet;
    }

    /// <summary>One box set of one library with its movies, in release order.</summary>
    public Task<CatalogBoxSetDetail> GetAsync(Uri server, string id, string libraryId, ImageParameters? images = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        string query = new QueryBuilder().Add("library_id", libraryId).Add(images).ToString();
        return http.GetAsync(server, $"api/v2/catalog/box-sets/{Segment(id)}{query}", SiloAuth.Profile,
            BoxSetsJsonContext.Default.CatalogBoxSetDetail, cancellationToken);
    }

    /// <summary>
    /// The box sets of libraries with box sets in search whose name, or two or more of whose movies' titles, match
    /// <paramref name="q"/>, best match first.
    /// </summary>
    public async Task<IReadOnlyList<CatalogBoxSet>> SearchAsync(Uri server, string q, int limit = SearchLimit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(q);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 20);
        string query = new QueryBuilder().Add("q", q.Trim()).Add("limit", limit).ToString();
        var list = await http.GetAsync(server, "api/v2/catalog/box-sets" + query, SiloAuth.Profile, BoxSetsJsonContext.Default.CatalogBoxSetList,
            cancellationToken).ConfigureAwait(false);
        return list.Items;
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
