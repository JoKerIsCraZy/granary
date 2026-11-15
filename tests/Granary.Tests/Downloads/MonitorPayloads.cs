using System.Globalization;

namespace Granary.Tests.Downloads;

/// <summary>The server's series monitor answers, shaped as its client guide shows them (downloads-api.md, 8).</summary>
internal static class MonitorPayloads
{
    /// <summary>One monitor of <paramref name="seriesId"/> with validator <paramref name="etag"/>.</summary>
    public static string Monitor(string id = "sub_1", string seriesId = "sr_1", string mode = "future", string etag = "\"m1\"",
        bool deleteWatched = false, long maxStorageBytes = 0, string? quality = "original", bool active = true, int? targetSeason = null,
        params int[] seasons)
    {
        string qualityMember = quality is null ? "" : $", \"quality\": \"{quality}\"";
        string target = targetSeason is { } season ? $", \"target_season\": {season.ToString(CultureInfo.InvariantCulture)}" : "";
        string escapedEtag = etag.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $$"""
            { "id": "{{id}}", "series_id": "{{seriesId}}", "mode": "{{mode}}", "season_numbers": [{{string.Join(", ", seasons)}}]{{target}},
              "delete_watched": {{Bool(deleteWatched)}}, "max_storage_bytes": {{maxStorageBytes.ToString(CultureInfo.InvariantCulture)}}{{qualityMember}},
              "active": {{Bool(active)}}, "created_at": "2026-11-01T10:00:00Z", "updated_at": "2026-11-01T10:00:00Z", "etag": "{{escapedEtag}}" }
            """;
    }

    /// <summary>A page of the monitor list; with <paramref name="nextCursor"/> more follow.</summary>
    public static string Page(string? nextCursor, params string[] monitors)
    {
        string page = nextCursor is null ? """{ "has_more": false }""" : $$"""{ "has_more": true, "next_cursor": "{{nextCursor}}" }""";
        return $$"""{ "items": [{{string.Join(", ", monitors)}}], "page": {{page}} }""";
    }

    /// <summary>A sync page for <paramref name="id"/> that registered <paramref name="registered"/> episodes.</summary>
    public static string Sync(string id = "sub_1", int registered = 0, string? nextCursor = null)
    {
        string page = nextCursor is null ? """{ "has_more": false }""" : $$"""{ "has_more": true, "next_cursor": "{{nextCursor}}" }""";
        return $$"""{ "subscription_id": "{{id}}", "registered": {{registered.ToString(CultureInfo.InvariantCulture)}}, "examined": 50, "page": {{page}} }""";
    }

    /// <summary>The capability with series monitoring on, its modes and whether monitors keep a quality.</summary>
    public static string Capability(bool monitoring = true, bool monitorQuality = true, params string[] modes)
    {
        string[] listed = modes.Length > 0 ? modes : ["all", "future", "latest_season", "specific_seasons"];
        string names = string.Join(", ", listed.Select(m => $"\"{m}\""));
        return DownloadPayloads.Capability(true, true, "original", "10mbps").Replace("\"season_download\": true,",
            $"\"season_download\": true, \"series_monitoring\": {Bool(monitoring)}, \"monitoring_modes\": [{names}], \"monitor_quality\": {Bool(monitorQuality)},",
            StringComparison.Ordinal);
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
