namespace Granary.Tests.Browse;

/// <summary>Bodies of the For You routes.</summary>
internal static class RecommendationPayloads
{
    private const string Heat = """
        { "content_id": "movie:heat-1995", "type": "movie", "title": "Heat", "year": 1995, "genres": ["Crime"], "keywords": [],
          "status": "matched", "poster_url": "https://s3.example.test/heat.jpg" }
        """;

    private const string Alien = """
        { "content_id": "movie:alien-1979", "type": "movie", "title": "Alien", "year": 1979, "genres": ["Horror"], "keywords": [],
          "status": "matched", "poster_url": "" }
        """;

    /// <summary>A row from the profile's history, a shared one, one no section lists, and an empty one.</summary>
    public const string Discover = $$"""
        {
          "items": [
            { "type": "cluster", "title": "Because you like crime", "kind": "cluster", "key": "0", "items": [ {{Heat}}, {{Alien}}, {{Heat}} ] },
            { "type": "popular", "title": "Popular on This Server", "kind": "popular", "items": [ {{Alien}} ] },
            { "type": "upcoming", "title": "Coming Up", "items": [ {{Heat}} ] },
            { "type": "top_rated", "title": "Highly Rated", "kind": "top-rated", "items": [] }
          ]
        }
        """;

    /// <summary>Only shared rows: a profile without history.</summary>
    public const string DiscoverShared = $$"""
        { "items": [ { "type": "popular", "title": "Popular on This Server", "kind": "popular", "items": [ {{Alien}} ] } ] }
        """;

    /// <summary>Rows Silo titled, with the key of each title: one with a value, one without.</summary>
    public const string DiscoverKeyed = $$"""
        {
          "items": [
            { "type": "genre", "title": "Top Crime", "title_key": "home.section.topGenre", "title_args": { "genre": "Krimi" },
              "kind": "genre", "key": "Crime", "items": [ {{Heat}} ] },
            { "type": "popular", "title": "Popular on This Server", "title_key": "home.section.popularOnServer", "kind": "popular", "items": [ {{Alien}} ] }
          ]
        }
        """;

    public const string NoRows = """{ "items": [] }""";

    public const string Taste = """
        { "top_genres": ["Crime", "Thriller"], "favorite_directors": ["Michael Mann"], "signal_counts": { "watched": 12 } }
        """;

    public const string NoTaste = """{ "top_genres": [], "favorite_directors": [], "signal_counts": {} }""";

    public const string Section = $$"""
        { "type": "popular", "title": "Popular on This Server", "kind": "popular", "items": [ {{Alien}}, {{Heat}} ] }
        """;

    public const string KeyedSection = $$"""
        { "type": "popular", "title": "Popular on This Server", "title_key": "home.section.popularOnServer", "kind": "popular", "items": [ {{Alien}} ] }
        """;

    public const string UntitledSection ="""{ "type": "genre", "title": "", "kind": "genre", "key": "Crime", "items": [] }""";
}
