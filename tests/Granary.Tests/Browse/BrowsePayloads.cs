using System.Text.Json;
using System.Text.Json.Serialization;

namespace Granary.Tests.Browse;

/// <summary>
/// Small response bodies shaped like the v2 schemas (CatalogItem, Section, CatalogBrowseCollection, Person),
/// for tests that need many rows or pages; Catalog/CatalogPayloads.cs holds the contract fixtures.
/// </summary>
internal static class BrowsePayloads
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static object Item(string id, string type = "movie", string? seriesId = null, string? progressUpdatedAt = null) => new
    {
        content_id = id,
        type,
        title = "Title " + id,
        status = "matched",
        genres = Array.Empty<string>(),
        keywords = Array.Empty<string>(),
        year = 2021,
        series_id = seriesId,
        series_title = seriesId is null ? null : "Series " + seriesId,
        season_number = type == "episode" ? 1 : (int?)null,
        episode_number = type == "episode" ? 2 : (int?)null,
        progress_updated_at = progressUpdatedAt,
        position_seconds = progressUpdatedAt is null ? (double?)null : 600,
        duration_seconds = progressUpdatedAt is null ? (double?)null : 3000,
        poster_url = $"/api/v2/artwork/{id}/poster.webp?exp=1&sig=s",
    };

    public static object Section(string id, string type, int total, params object[] items) => new
    {
        id,
        section_type = type,
        title = "Row " + id,
        featured = false,
        item_limit = 20,
        is_custom = false,
        customized = false,
        total_count = total,
        items,
    };

    public static string Sections(params object[] sections) => Serialize(new { sections });

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    /// <summary>One browse page; <paramref name="next"/> null is the last page.</summary>
    public static string Page(string? next, params object[] items) => Serialize(new
    {
        items,
        page = new { has_more = next is not null, next_cursor = next },
        total = items.Length,
        total_exact = true,
        window_cursor = "w",
    });

    public static string People(params (string Id, string Name)[] people) =>
        Serialize(new { items = people.Select(p => new { id = p.Id, name = p.Name, photo_url = $"/api/v2/artwork/people/{p.Id}.webp?sig=p" }) });

    public static string SearchCapabilities(bool people) => Serialize(new
    {
        revision = "r1",
        state = "available",
        allowed = true,
        provider = "meilisearch",
        people_media_scope = people,
        video_with_episodes_scope = false,
    });
}
