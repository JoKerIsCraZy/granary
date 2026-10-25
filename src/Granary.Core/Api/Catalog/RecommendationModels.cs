namespace Granary.Core.Api.Catalog;

/// <summary><c>GET /api/v2/recommendations/discover</c>: the For You page's rows with their cards.</summary>
public sealed record RecommendationRowCollection
{
    public IReadOnlyList<RecommendationRow> Items { get; init => field = value ?? []; } = [];
}

/// <summary>
/// A recommendation row; also the answer of <c>GET /api/v2/recommendations/section/{kind}</c>, one row in full.
/// </summary>
public sealed record RecommendationRow
{
    /// <summary>Open set, see <see cref="RecommendationRowTypes"/>.</summary>
    public string Type { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";

    /// <summary>The section that lists the row in full (see <see cref="RecommendationSectionKinds"/>); absent when none does.</summary>
    public string? Kind { get; init; }

    /// <summary>Goes with <see cref="Kind"/> when the section needs it: a cluster's index, a genre's name.</summary>
    public string? Key { get; init; }

    public IReadOnlyList<CatalogItem> Items { get; init => field = value ?? []; } = [];
}

/// <summary><c>GET /api/v2/recommendations/taste-profile</c> (only what the For You page shows).</summary>
public sealed record TasteProfile
{
    public IReadOnlyList<string> TopGenres { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> FavoriteDirectors { get; init => field = value ?? []; } = [];
}

/// <summary>Values of <see cref="RecommendationRow.Type"/> that come from the profile's own history.</summary>
public static class RecommendationRowTypes
{
    public const string Cluster = "cluster";

    public const string SimilarUsersLiked = "similar_users_liked";

    public static bool IsPersonal(string type) => type is Cluster or SimilarUsersLiked;
}

/// <summary>The sections <c>GET /api/v2/recommendations/section/{kind}</c> serves.</summary>
public static class RecommendationSectionKinds
{
    public static IReadOnlyList<string> All { get; } =
        ["for-you-main", "cluster", "similar-users", "popular", "recently-added", "top-rated", "genre"];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}
