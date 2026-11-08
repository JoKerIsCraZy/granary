namespace Granary.Tests.Browse;

/// <summary>
/// Box set answers as Silo Server's <c>internal/apiv2/catalog_box_sets.go</c> writes them: ids as strings, the movies in
/// release order. The movie page's box set holds <see cref="ItemDetailPayloads.MovieId"/> in the middle.
/// </summary>
internal static class BoxSetPayloads
{
    public const string BoxSetId = "1241";
    public const string LibraryId = "3";
    public const string FirstId = "movie-tmdb-1000";
    public const string LastId = "movie-tmdb-1003";

    /// <summary>The box set of <see cref="ItemDetailPayloads.MovieId"/>: three films, the first one played.</summary>
    public const string ItemBoxSet = """
        { "box_set": {
            "id": "1241", "library_id": "3", "title": "Masterplan Box Set", "overview": "Three jobs, one crew.",
            "poster_url": "/api/v2/artwork/box-sets/1241/poster/w342.webp?exp=1&sig=bp",
            "poster_thumbhash": "1QcSHQRnh493V4dIh4eXh1h4kJUI",
            "backdrop_url": "https://image.tmdb.org/t/p/original/masterplan-collection.jpg",
            "movie_count": 3, "played_count": 1, "first_year": 2019, "last_year": 2028,
            "items": [
              { "content_id": "movie-tmdb-1000", "type": "movie", "title": "Groundwork", "year": 2019, "genres": [], "keywords": [],
                "status": "matched", "poster_url": "/api/v2/artwork/movies/1000/poster/w342.webp?exp=1&sig=a",
                "user_state": { "played": true, "is_favorite": false, "in_watchlist": false, "not_interested": false } },
              { "content_id": "movie-tmdb-1001", "type": "movie", "title": "Masterplan", "year": 2026, "genres": [], "keywords": [],
                "status": "matched", "poster_url": "/api/v2/artwork/movies/1001/poster/w342.webp?exp=1&sig=b",
                "position_seconds": 1200, "duration_seconds": 6540,
                "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false } },
              { "content_id": "movie-tmdb-1003", "type": "movie", "title": "Endgame", "year": 2028, "genres": [], "keywords": [],
                "status": "matched", "overlay_summary": { "resolution": "2160p" },
                "user_state": { "played": false, "is_favorite": false, "in_watchlist": false, "not_interested": false } }
            ] } }
        """;

    /// <summary>A movie in no box set the viewer sees: the server leaves <c>box_set</c> out.</summary>
    public const string NoBoxSet = "{}";

    /// <summary>The box set page's answer: the same three films, <paramref name="played"/> of them from the first played.</summary>
    public static string BoxSet(int played = 1, double? nextPosition = null) => $$"""
        { "id": "1241", "library_id": "3", "title": "Masterplan Box Set", "overview": "Three jobs, one crew.",
          "poster_url": "/api/v2/artwork/box-sets/1241/poster/w342.webp?exp=1&sig=bp",
          "backdrop_url": "/api/v2/artwork/box-sets/1241/backdrop/w1280.webp?exp=1&sig=bb", "backdrop_thumbhash": "3OcRJYB4d3h/iIeHeEh3eIhw+j3A",
          "movie_count": 3, "played_count": {{played}}, "first_year": 2019, "last_year": 2028,
          "items": [
            {{Film("movie-tmdb-1000", "Groundwork", 2019, played >= 1, nextPosition is { } p0 && played == 0 ? p0 : null)}},
            {{Film("movie-tmdb-1001", "Masterplan", 2026, played >= 2, nextPosition is { } p1 && played == 1 ? p1 : null)}},
            {{Film("movie-tmdb-1003", "Endgame", 2028, played >= 3, nextPosition is { } p2 && played == 2 ? p2 : null)}}
          ] }
        """;

    /// <summary>What search finds for "masterplan": the box set, watched in part, and another without artwork.</summary>
    public const string SearchList = """
        { "items": [
            { "id": "1241", "library_id": "3", "title": "Masterplan Box Set",
              "poster_url": "/api/v2/artwork/box-sets/1241/poster/w342.webp?exp=1&sig=bp", "poster_thumbhash": "1QcSHQRnh493V4dIh4eXh1h4kJUI",
              "movie_count": 3, "played_count": 1, "first_year": 2019, "last_year": 2028 },
            { "id": "88", "library_id": "4", "title": "Groundwork Collection", "movie_count": 2, "played_count": 0, "first_year": 2019, "last_year": 2019 }
          ] }
        """;

    /// <summary>The profile's libraries: movies, with box sets on or off, and series, which have none.</summary>
    public static string Libraries(bool boxSets) => $$"""
        { "items": [
            { "id": "3", "name": "Films", "type": "movies", "sort_order": 1, "box_sets_enabled": {{(boxSets ? "true" : "false")}} },
            { "id": "4", "name": "Series", "type": "series", "sort_order": 2, "box_sets_enabled": false }
          ] }
        """;

    private static string Film(string id, string title, int year, bool played, double? position) => $$"""
        { "content_id": "{{id}}", "type": "movie", "title": "{{title}}", "year": {{year}}, "genres": [], "keywords": [], "status": "matched",
          {{(position is { } at ? $"\"position_seconds\": {at.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"duration_seconds\": 6000," : "")}}
          "user_state": { "played": {{(played ? "true" : "false")}}, "is_favorite": false, "in_watchlist": false, "not_interested": false } }
        """;
}
