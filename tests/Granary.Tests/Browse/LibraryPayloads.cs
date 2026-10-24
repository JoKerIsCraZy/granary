namespace Granary.Tests.Browse;

/// <summary>
/// Library page responses: from the server's contract fixtures where one exists (named per constant), else
/// built from the OpenAPI schemas.
/// </summary>
internal static class LibraryPayloads
{
    /// <summary>A movie, a series and an audiobook library.</summary>
    public const string Libraries = """
        {
          "items": [
            { "id": "1", "name": "Filme", "type": "movies", "sort_order": 1 },
            { "id": "2", "name": "Serien", "type": "series", "sort_order": 2 },
            { "id": "3", "name": "Hörbücher", "type": "audiobooks", "sort_order": 3 }
          ]
        }
        """;

    /// <summary>
    /// Library 1's rows: one with cards (<c>list_library_sections_ok.json</c>), one with nothing at all, and one
    /// that came back without cards but says it has twelve.
    /// </summary>
    public const string Sections = """
        {
          "sections": [
            {
              "id": "continue_watching", "section_type": "continue_watching", "title": "Continue Watching",
              "featured": false, "item_limit": 20, "total_count": 1, "is_custom": false, "customized": false,
              "items": [
                {
                  "content_id": "movie:heat-1995", "type": "movie", "title": "Heat", "year": 1995, "runtime": 170,
                  "genres": ["Crime"], "keywords": [], "content_rating": "R", "status": "matched",
                  "position_seconds": 1200.5, "duration_seconds": 10200, "progress_updated_at": "2026-01-02T03:04:05.000Z",
                  "poster_url": "https://s3.example.test/poster.jpg", "item_source": "in_progress"
                }
              ]
            },
            {
              "id": "watchlist", "section_type": "watchlist", "title": "Your Watchlist",
              "featured": false, "item_limit": 20, "total_count": 0, "is_custom": false, "customized": false, "items": []
            },
            {
              "id": "recently_added", "section_type": "recently_added", "title": "Recently Added",
              "featured": false, "item_limit": 20, "total_count": 12, "is_custom": false, "customized": false, "items": []
            }
          ]
        }
        """;

    /// <summary><c>get_library_section_items_ok.json</c>-shaped: the twelve-card row, two of its cards.</summary>
    public const string RecentlyAddedItems = """
        {
          "id": "recently_added", "section_type": "recently_added", "title": "Recently Added",
          "featured": false, "item_limit": 20, "total_count": 12, "is_custom": false, "customized": false,
          "items": [
            { "content_id": "movie:alien-1979", "type": "movie", "title": "Alien", "year": 1979, "genres": [], "keywords": [], "status": "matched" },
            { "content_id": "movie:heat-1995", "type": "movie", "title": "Heat", "year": 1995, "genres": [], "keywords": [], "status": "matched" }
          ]
        }
        """;

    /// <summary>The first browse page: two titles and a cursor to the next.</summary>
    public const string FirstPage = """
        {
          "window_cursor": "w1",
          "items": [
            { "content_id": "movie:alien-1979", "type": "movie", "title": "Alien", "year": 1979, "genres": ["Horror"], "keywords": [], "status": "matched" },
            { "content_id": "movie:heat-1995", "type": "movie", "title": "Heat", "year": 1995, "genres": ["Crime"], "keywords": [], "status": "matched" }
          ],
          "page": { "next_cursor": "c2", "has_more": true },
          "total": 1234,
          "total_exact": true
        }
        """;

    /// <summary>The last page: one title, no cursor.</summary>
    public const string LastPage = """
        {
          "window_cursor": "w1",
          "items": [
            { "content_id": "movie:se7en-1995", "type": "movie", "title": "Se7en", "year": 1995, "genres": ["Crime"], "keywords": [], "status": "matched" }
          ],
          "page": { "has_more": false },
          "total": 1234,
          "total_exact": true
        }
        """;

    public const string EmptyPage = """
        { "window_cursor": "w0", "items": [], "page": { "has_more": false }, "total": 0, "total_exact": true }
        """;

    /// <summary><c>get_catalog_filters_ok.json</c> with two genres and two ratings.</summary>
    public const string Filters = """
        {
          "genres": ["Crime", "Drama"],
          "studios": [], "networks": [], "countries": [], "original_languages": [],
          "content_ratings": ["PG-13", "R"],
          "authors": [], "narrators": [], "series": []
        }
        """;

    /// <summary><c>get_library_collections_ok.json</c>.</summary>
    public const string Collections = """
        {
          "library_id": "1",
          "collections": [
            {
              "id": "c1", "library_id": "1", "library_ids": ["1"], "slug": "oscar-winners", "title": "Oscar Winners",
              "description": "", "collection_type": "manual", "visibility": "visible", "sort_order": 0, "group_id": null,
              "featured": false, "poster_url": "", "backdrop_url": "", "source_url": "", "query_definition": { "filters": [] },
              "sort_config": null, "source_config": null, "management_mode": "manual", "management_source": "", "management_key": "",
              "last_sync_status": "", "last_sync_message": "", "item_count": 12,
              "created_at": "2026-01-02T03:04:05.000Z", "updated_at": "2026-01-02T03:04:05.000Z", "poster_is_collage": false
            }
          ],
          "groups": [
            {
              "id": "g1", "name": "Mine", "kind": "user_collections", "sort_mode": "name_asc", "sort_order": 0,
              "collections": [
                { "id": "u1", "title": "Rainy days", "poster_url": "", "item_count": 4, "creator_profile_id": "p-owner", "poster_is_collage": false }
              ]
            }
          ],
          "ungrouped": {
            "sort_order": 9999,
            "collections": [
              { "id": "c1", "title": "Oscar Winners", "poster_url": "/api/v2/artwork/collections/c1/poster.webp?exp=1&sig=s", "item_count": 12, "featured": true, "poster_is_collage": false }
            ]
          }
        }
        """;

    /// <summary>A server without collection groups: the collections only in full.</summary>
    public const string CollectionsWithoutGroups = """
        {
          "library_id": "1",
          "collections": [
            { "id": "c2", "title": "Heists", "poster_url": "", "item_count": 1, "sort_order": 2 },
            { "id": "c1", "title": "Oscar Winners", "poster_url": "", "item_count": 12, "sort_order": 1 }
          ],
          "groups": []
        }
        """;

    public const string NoCollections = """{ "library_id": "1", "collections": [], "groups": [] }""";

    /// <summary>The <c>ShuffleCapability</c> of a server that can shuffle every scope.</summary>
    public const string ShuffleAvailable = """
        { "scope_kinds": ["library", "series", "season", "library_collection", "user_collection"], "revision": "r1", "state": "available", "allowed": true }
        """;

    public const string ShuffleNotConfigured = """
        { "scope_kinds": [], "revision": "r1", "state": "not_configured", "allowed": true }
        """;

    /// <summary>A started <c>Shuffle</c> of library 1.</summary>
    public const string Shuffle = """
        {
          "id": "17",
          "scope": { "kind": "library", "id": "1", "title": "Filme" },
          "current": { "content_id": "movie:alien-1979", "type": "movie", "title": "Alien", "genres": [], "keywords": [], "status": "matched" },
          "next": { "content_id": "movie:heat-1995", "type": "movie", "title": "Heat", "genres": [], "keywords": [], "status": "matched" },
          "created_at": "2026-01-02T03:04:05.000Z",
          "updated_at": "2026-01-02T03:04:05.000Z"
        }
        """;
}
