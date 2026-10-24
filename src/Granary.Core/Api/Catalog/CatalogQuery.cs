using Granary.Core.Api.Images;

namespace Granary.Core.Api.Catalog;

/// <summary>
/// The parameters of <c>GET /api/v2/catalog</c> (docs/api-contract.md, 7.4). Unset members are left out
/// of the request. The next page is the same query with the page's cursor (<see cref="WithCursor"/>):
/// cursors are bound to every other parameter, including <see cref="Images"/>.
/// </summary>
public sealed record CatalogQuery
{
    /// <summary>Page size 1..200; the server's default is 50.</summary>
    public const int MaximumLimit = 200;

    /// <summary>What to page, see <see cref="CatalogSources"/>; the server's default is the whole catalog (<c>query</c>).</summary>
    public string? Source { get; init; }

    /// <summary>For the section source (ignored by the others): <see cref="CatalogScopes"/>; the server's default is <c>library</c>.</summary>
    public string? Scope { get; init; }

    public string? LibraryId { get; init; }

    public string? SectionId { get; init; }

    public string? CollectionId { get; init; }

    public string? PersonId { get; init; }

    /// <summary>Search text; results come in relevance order unless <see cref="Sort"/> says otherwise.</summary>
    public string? Q { get; init; }

    /// <summary>A–Z jump: titles whose sort title starts here.</summary>
    public string? NamePrefix { get; init; }

    /// <summary>
    /// <see cref="MediaScopes"/>. The server ignores an unknown value and lists everything, so send only
    /// the documented ones.
    /// </summary>
    public string? Type { get; init; }

    public string? Genre { get; init; }

    /// <summary>Metadata match state.</summary>
    public string? Status { get; init; }

    public int? YearMin { get; init; }

    public int? YearMax { get; init; }

    /// <summary>Any of these ratings (<c>content_rating</c>, one key per rating).</summary>
    public IReadOnlyList<string>? ContentRatings { get; init; }

    public CatalogSort? Sort { get; init; }

    /// <summary><c>work</c> collapses the editions of one book into one card.</summary>
    public string? Group { get; init; }

    public int? Limit { get; init; }

    public string? Cursor { get; init; }

    /// <summary>Skips the exact count; <see cref="CatalogBrowseCollection.Total"/> is then an estimate.</summary>
    public bool SkipTotal { get; init; }

    /// <summary>Artwork sizes, from <see cref="ImageSizes.For"/>.</summary>
    public ImageParameters Images { get; init; } = ImageParameters.None;

    /// <summary>"See all" of a Home row: the row's own order, paged, without filters or sorting.</summary>
    public static CatalogQuery ForHomeSection(string sectionId) =>
        new() { Source = CatalogSources.Section, Scope = CatalogScopes.Home, SectionId = sectionId };

    /// <summary>"See all" of a library row: the row's own order, paged, without filters or sorting.</summary>
    public static CatalogQuery ForLibrarySection(string libraryId, string sectionId) =>
        new() { Source = CatalogSources.Section, Scope = CatalogScopes.Library, LibraryId = libraryId, SectionId = sectionId };

    /// <summary>
    /// All titles of a library, its free-text type mapped to <see cref="Type"/> (docs/api-contract.md, 7.3):
    /// movie(s) → movie, series, show(s) or tv → series, anything else (mixed, …) unscoped.
    /// </summary>
    public static CatalogQuery ForLibrary(Library library)
    {
        ArgumentNullException.ThrowIfNull(library);
        string? type = (library.Type ?? "").Trim().ToLowerInvariant() switch
        {
            "movies" or "movie" => MediaScopes.Movie,
            "series" or "show" or "shows" or "tv" => MediaScopes.Series,
            _ => null,
        };
        return new() { LibraryId = library.Id, Type = type };
    }

    /// <summary>A person's titles, newest first.</summary>
    public static CatalogQuery ForPerson(string personId) =>
        new() { Source = CatalogSources.Person, PersonId = personId, Sort = CatalogSort.Descending(CatalogSortFields.Year) };

    /// <summary>The same query for the page after <paramref name="cursor"/>; null starts from page one.</summary>
    public CatalogQuery WithCursor(string? cursor) => this with { Cursor = cursor };

    /// <summary>The query string with its leading <c>?</c> (or empty).</summary>
    /// <exception cref="ArgumentException">The combination is one the server refuses with 422.</exception>
    public string ToQueryString()
    {
        Validate();
        return new QueryBuilder()
            .Add("source", Source)
            .Add("scope", Scope)
            .Add("section_id", SectionId)
            .Add("library_id", LibraryId)
            .Add("collection_id", CollectionId)
            .Add("person_id", PersonId)
            .Add("q", Q)
            .Add("name_prefix", NamePrefix)
            .Add("type", Type)
            .Add("genre", Genre)
            .Add("status", Status)
            .Add("year_min", YearMin)
            .Add("year_max", YearMax)
            .AddEach("content_rating", ContentRatings)
            .Add("sort", Sort?.ToString())
            .Add("group", Group)
            .Add("limit", Limit)
            .Add("cursor", Cursor)
            .AddFlag("skip_total", SkipTotal)
            .Add(Images)
            .ToString();
    }

    /// <summary>The scope members only, for <c>GET /api/v2/catalog/filters</c>.</summary>
    internal QueryBuilder ScopeQuery() => new QueryBuilder()
        .Add("source", Source)
        .Add("scope", Scope)
        .Add("section_id", SectionId)
        .Add("library_id", LibraryId)
        .Add("collection_id", CollectionId)
        .Add("person_id", PersonId)
        .Add("type", Type);

    private void Validate()
    {
        if (Limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), Limit, $"The page size must be between 1 and {MaximumLimit}.");
        }
        if (YearMin < 0 || YearMax < 0)
        {
            throw new ArgumentOutOfRangeException(YearMin < 0 ? nameof(YearMin) : nameof(YearMax), "A year cannot be negative.");
        }
        if (IsSet(Group) && !Is(Group, "work"))
        {
            throw new ArgumentException($"Unknown grouping '{Group}'.", nameof(Group));
        }
        Sort?.Validate();
        // The server trims and lower-cases the source; an absent one is the whole catalog.
        bool isQuery = !IsSet(Source) || Is(Source, CatalogSources.Query);
        if (Is(Source, CatalogSources.Section))
        {
            ValidateSectionListing();
        }
        else if (Is(Source, CatalogSources.Person) && !IsSet(PersonId))
        {
            throw new ArgumentException("A person listing needs a person id.", nameof(PersonId));
        }
        else if ((Is(Source, CatalogSources.LibraryCollection) || Is(Source, CatalogSources.UserCollection)) && !IsSet(CollectionId))
        {
            throw new ArgumentException("A collection listing needs a collection id.", nameof(CollectionId));
        }
        if (Is(Type, MediaScopes.VideoWithEpisodes) && !isQuery)
        {
            throw new ArgumentException("video_with_episodes applies only to the query source.", nameof(Type));
        }
        if (Sort is { } sort && Is(sort.Field, CatalogSortFields.Relevance) && !(isQuery && IsSet(Q)))
        {
            throw new ArgumentException("Relevance order needs search text on the query source.", nameof(Sort));
        }
    }

    /// <summary>
    /// A section listing keeps the row's order and takes only limit, cursor, skip_total, group and image
    /// parameters besides its ids (docs/api-contract.md, 7.1): the server answers search text, filters,
    /// a sort, and a library id on a Home row with 422.
    /// </summary>
    private void ValidateSectionListing()
    {
        if (!IsSet(SectionId))
        {
            throw new ArgumentException("A section listing needs a section id.", nameof(SectionId));
        }
        if (IsSet(Scope) && !Is(Scope, CatalogScopes.Library) && !Is(Scope, CatalogScopes.Home))
        {
            throw new ArgumentException($"Unknown section scope '{Scope}'.", nameof(Scope));
        }
        bool isHome = Is(Scope, CatalogScopes.Home);
        if (!isHome && !IsSet(LibraryId))
        {
            throw new ArgumentException("A library section listing needs a library id.", nameof(LibraryId));
        }
        (string Name, bool Given)[] refused =
        [
            (nameof(LibraryId), isHome && IsSet(LibraryId)),
            (nameof(Q), IsSet(Q)),
            (nameof(NamePrefix), IsSet(NamePrefix)),
            (nameof(Type), IsSet(Type)),
            (nameof(Genre), IsSet(Genre)),
            (nameof(Status), IsSet(Status)),
            (nameof(YearMin), YearMin is not null),
            (nameof(YearMax), YearMax is not null),
            (nameof(ContentRatings), ContentRatings is { Count: > 0 }),
            (nameof(Sort), Sort is not null),
        ];
        foreach (var (name, given) in refused)
        {
            if (given)
            {
                throw new ArgumentException($"A section listing does not accept {name}.", name);
            }
        }
    }

    private static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool Is(string? value, string expected) => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One sort term, sent as <c>field</c> (ascending) or <c>-field</c> (descending).</summary>
/// <param name="Field">One of <see cref="CatalogSortFields"/>.</param>
public sealed record CatalogSort(string Field, bool IsDescending = false)
{
    public static CatalogSort Ascending(string field) => new(field);

    public static CatalogSort Descending(string field) => new(field, IsDescending: true);

    public override string ToString() => IsDescending ? "-" + Field : Field;

    internal void Validate()
    {
        if (string.IsNullOrEmpty(Field) || Field[0] == '-' || Field.Contains(',', StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{Field}' is not a single sort field.", nameof(Field));
        }
    }
}

/// <summary>Values of <see cref="CatalogQuery.Source"/>.</summary>
public static class CatalogSources
{
    public const string Query = "query";
    public const string Section = "section";
    public const string LibraryCollection = "library_collection";
    public const string UserCollection = "user_collection";
    public const string Favorites = "favorites";
    public const string Watchlist = "watchlist";
    public const string History = "history";
    public const string Person = "person";
}

/// <summary>Values of <see cref="CatalogQuery.Scope"/>.</summary>
public static class CatalogScopes
{
    public const string Home = "home";
    public const string Library = "library";
}

/// <summary>Sort fields the browse accepts (personalized ones included); unknown ones are a 422.</summary>
public static class CatalogSortFields
{
    public const string Title = "title";
    public const string AddedAt = "added_at";
    public const string ReleaseDate = "release_date";
    public const string LastAirDate = "last_air_date";

    /// <summary>When the newest episode file of a series arrived.</summary>
    public const string LatestEpisodeAdded = "latest_episode_added";
    public const string Year = "year";
    public const string ContentRating = "content_rating";
    public const string Runtime = "runtime";
    public const string RatingImdb = "rating_imdb";
    public const string RatingTmdb = "rating_tmdb";
    public const string Resolution = "resolution";
    public const string Bitrate = "bitrate";
    public const string Progress = "progress";
    public const string DateViewed = "date_viewed";
    public const string Plays = "plays";

    /// <summary>Only together with <see cref="CatalogQuery.Q"/>.</summary>
    public const string Relevance = "relevance";
}
