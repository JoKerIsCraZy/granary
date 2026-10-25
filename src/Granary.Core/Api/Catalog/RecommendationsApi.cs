using Granary.Core.Api.Images;

namespace Granary.Core.Api.Catalog;

/// <summary>The For You page's calls, for the selected profile. "More like this" is <see cref="CatalogApi.ListSimilarAsync"/>.</summary>
public sealed class RecommendationsApi(SiloHttp http)
{
    /// <summary>The profile's recommendation rows with their cards.</summary>
    public Task<RecommendationRowCollection> GetDiscoverAsync(Uri server, ImageParameters? images = null, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/recommendations/discover" + new QueryBuilder().Add(images), SiloAuth.Profile,
            SiloJsonContext.Default.RecommendationRowCollection, cancellationToken);

    /// <summary>One row in full (up to 60 cards); <paramref name="kind"/> is one of <see cref="RecommendationSectionKinds"/>.</summary>
    public Task<RecommendationRow> GetSectionAsync(Uri server, string kind, string? key = null, ImageParameters? images = null,
        CancellationToken cancellationToken = default)
    {
        if (!RecommendationSectionKinds.IsKnown(kind))
        {
            throw new ArgumentException($"'{kind}' is not a recommendation section.", nameof(kind));
        }
        string query = new QueryBuilder().Add("key", key).Add(images).ToString();
        return http.GetAsync(server, $"api/v2/recommendations/section/{kind}{query}", SiloAuth.Profile,
            SiloJsonContext.Default.RecommendationRow, cancellationToken);
    }

    /// <summary>The genres and directors the profile's history leans to.</summary>
    public Task<TasteProfile> GetTasteProfileAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/recommendations/taste-profile", SiloAuth.Profile, SiloJsonContext.Default.TasteProfile, cancellationToken);
}
