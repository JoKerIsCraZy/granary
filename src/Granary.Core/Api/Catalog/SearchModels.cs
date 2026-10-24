namespace Granary.Core.Api.Catalog;

/// <summary>
/// <c>GET /api/v2/catalog/search/capabilities</c> (docs/api-contract.md, 7.5). Title search itself is
/// <see cref="CatalogQuery.Q"/>; send <c>type=video_with_episodes</c> only when
/// <see cref="VideoWithEpisodesScope"/> is true, and search people only when <see cref="PeopleMediaScope"/> is.
/// </summary>
public sealed record CatalogSearchCapabilities
{
    public string State { get; init => field = value ?? ""; } = "";

    public bool Allowed { get; init; }

    public string Revision { get; init => field = value ?? ""; } = "";

    /// <summary><c>postgres</c> or <c>meilisearch</c>.</summary>
    public string? Provider { get; init; }

    public bool PeopleMediaScope { get; init; }

    public bool VideoWithEpisodesScope { get; init; }

    /// <summary>Maximum candidates in a Meilisearch ranked window.</summary>
    public int? ResultWindowLimit { get; init; }

    /// <summary>Lifetime of a Meilisearch ranking session; an expired one answers <c>invalid_cursor</c>.</summary>
    public int? SessionTtlSeconds { get; init; }

    public bool IsAvailable => State == "available";
}

/// <summary><c>GET /api/v2/catalog/people</c>.</summary>
public sealed record PersonCollection(IReadOnlyList<Person> Items, PageInfo? Page);

/// <summary>A cast or crew member. Their titles: <see cref="CatalogQuery.ForPerson"/>.</summary>
public sealed record Person
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string Name { get; init => field = value ?? ""; } = "";

    public string? PhotoUrl { get; init; }

    public string? PhotoThumbhash { get; init; }

    public string? Bio { get; init; }

    public string? BirthDate { get; init; }

    public string? DeathDate { get; init; }

    public string? Birthplace { get; init; }
}

/// <summary>Values of the browse <c>type</c> filter and of the people search <c>media_scope</c>.</summary>
public static class MediaScopes
{
    public const string Movie = "movie";
    public const string Series = "series";
    public const string Episode = "episode";

    /// <summary>Movies and series.</summary>
    public const string Video = "video";

    /// <summary>Movies, series and episodes; only when <see cref="CatalogSearchCapabilities.VideoWithEpisodesScope"/> is true, and for browsing only with the query source.</summary>
    public const string VideoWithEpisodes = "video_with_episodes";

    public const string Audiobook = "audiobook";
    public const string Ebook = "ebook";
    public const string Manga = "manga";
}
