namespace Granary.Tests.Browse;

/// <summary>Bodies of the Collections page's routes, for the profile "7" of <see cref="LibraryHarness"/>.</summary>
internal static class CollectionPayloads
{
    /// <summary>
    /// Two of the profile's own (one shared, one a synced list whose sync failed), then one shared by Mara and one by a
    /// profile no longer on the login.
    /// </summary>
    public const string Personal = """
        {
          "groups": [],
          "items": [
            {
              "id": "u1", "name": "Rainy days", "collection_type": "manual", "creator_profile_id": "7", "profile_id": "7",
              "is_shared": true, "item_count": 4, "poster_url": "/api/v2/artwork/user-collections/u1/poster.webp?sig=s",
              "poster_is_collage": false, "poster_thumbhash": "", "description": "", "group_id": null,
              "include_in_server_collections": false, "last_sync_at": null, "last_sync_message": "", "last_sync_status": "",
              "next_sync_at": null, "created_at": "2026-01-02T03:04:05.678Z", "query_definition": {}
            },
            {
              "id": "u2", "name": "Top Picks", "collection_type": "mdblist", "creator_profile_id": "7", "profile_id": "7",
              "is_shared": false, "item_count": 1, "poster_url": "", "poster_is_collage": false, "description": "",
              "group_id": null, "include_in_server_collections": false, "last_sync_at": null,
              "last_sync_message": "List not found", "last_sync_status": "failed", "next_sync_at": null,
              "created_at": "2026-01-02T03:04:05.678Z", "query_definition": {}
            },
            {
              "id": "u4", "name": "Old favourites", "collection_type": "smart", "creator_profile_id": "99", "profile_id": "7",
              "is_shared": true, "item_count": 9, "poster_url": "", "poster_is_collage": false, "description": "",
              "group_id": null, "include_in_server_collections": false, "last_sync_at": null, "last_sync_message": "",
              "last_sync_status": "", "next_sync_at": null, "created_at": "2026-01-02T03:04:05.678Z", "query_definition": {}
            },
            {
              "id": "u3", "name": "Cartoons", "collection_type": "manual", "creator_profile_id": "8", "profile_id": "7",
              "is_shared": true, "item_count": 2, "poster_url": "", "poster_is_collage": false, "description": "",
              "group_id": null, "include_in_server_collections": false, "last_sync_at": null, "last_sync_message": "",
              "last_sync_status": "", "next_sync_at": null, "created_at": "2026-01-02T03:04:05.678Z", "query_definition": {}
            }
          ]
        }
        """;

    public const string NoPersonal = """{ "groups": [], "items": [] }""";

    /// <summary>The profile and Mara.</summary>
    public const string Profiles = """
        {
          "items": [
            { "id": "8", "name": "Mara", "is_primary": true, "has_pin": false, "is_child": false },
            { "id": "7", "name": "Elena", "is_primary": false, "has_pin": false, "is_child": false }
          ],
          "avatar_upload_enabled": false
        }
        """;

    public const string OneProfile = """
        {
          "items": [ { "id": "7", "name": "Elena", "is_primary": true, "has_pin": false, "is_child": false } ],
          "avatar_upload_enabled": false
        }
        """;

    /// <summary>
    /// Filme's first two of three, and Serien's first two of three; Bond is in both, so it opens across both.
    /// </summary>
    public const string Server = """
        {
          "libraries": [
            {
              "library_id": "1", "library_name": "Filme", "total_count": 3,
              "collections": [
                { "id": "s1", "title": "Oscar Winners", "poster_url": "/api/v2/artwork/collections/s1/poster.webp?sig=s", "item_count": 12, "poster_is_collage": false },
                { "id": "s2", "title": "Bond", "poster_url": "", "item_count": 25, "poster_is_collage": false }
              ]
            },
            {
              "library_id": "2", "library_name": "Serien", "total_count": 3,
              "collections": [
                { "id": "s2", "title": "Bond", "poster_url": "", "item_count": 25, "poster_is_collage": false },
                { "id": "s4", "title": "Anime", "poster_url": "", "item_count": 1, "poster_is_collage": false }
              ]
            }
          ]
        }
        """;

    /// <summary>One library, whose first few are all it has.</summary>
    public const string ServerOneLibrary = """
        {
          "libraries": [
            {
              "library_id": "1", "library_name": "Filme", "total_count": 1,
              "collections": [ { "id": "s1", "title": "Oscar Winners", "poster_url": "", "item_count": 12, "poster_is_collage": false } ]
            }
          ]
        }
        """;

    public const string FilmeTab = """
        {
          "library_id": "1",
          "collections": [
            { "id": "s1", "title": "Oscar Winners", "poster_url": "", "item_count": 12, "sort_order": 0 },
            { "id": "s2", "title": "Bond", "poster_url": "", "item_count": 25, "sort_order": 1 },
            { "id": "s3", "title": "Pixar", "poster_url": "", "item_count": 20, "sort_order": 2 }
          ],
          "groups": []
        }
        """;

    public const string SerienTab = """
        {
          "library_id": "2",
          "collections": [
            { "id": "s2", "title": "Bond", "poster_url": "", "item_count": 25, "sort_order": 0 },
            { "id": "s4", "title": "Anime", "poster_url": "", "item_count": 1, "sort_order": 1 },
            { "id": "s5", "title": "Sitcoms", "poster_url": "", "item_count": 7, "sort_order": 2 }
          ],
          "groups": []
        }
        """;
}
