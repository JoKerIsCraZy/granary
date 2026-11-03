using System.Text.Json;

namespace Granary.Core.Api.Catalog;

/// <summary>
/// The JSON bodies of the listings that take rule groups: <c>POST /api/v2/catalog/query</c> (<see cref="CatalogQueryBody"/>)
/// and the letter index (<see cref="CatalogLettersBody"/>). Both take a query's simple filters as the rules the server
/// makes of them on <c>GET /api/v2/catalog</c> (docs/api-contract.md, 7.4), then its <see cref="CatalogQuery.Rules"/>.
/// </summary>
internal static class CatalogRuleWriter
{
    /// <summary>An object whose members <paramref name="write"/> writes.</summary>
    public static JsonElement Body(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>A member with a value; nothing for a blank one.</summary>
    public static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>
    /// <c>groups</c>: genre, status and years in one <c>all</c> group, the ratings in another (several are alternatives),
    /// then the query's own rule groups.
    /// </summary>
    public static void WriteGroups(Utf8JsonWriter writer, CatalogQuery query)
    {
        writer.WriteStartArray("groups");
        WriteFilters(writer, query);
        WriteRatings(writer, query.ContentRatings ?? []);
        foreach (var group in query.Rules ?? [])
        {
            StartGroup(writer, group.Match);
            foreach (var rule in group.Rules)
            {
                WriteRule(writer, rule.Field, rule.Op, rule.Value.WriteTo);
            }
            EndGroup(writer);
        }
        writer.WriteEndArray();
    }

    private static void WriteFilters(Utf8JsonWriter writer, CatalogQuery query)
    {
        bool hasGenre = !string.IsNullOrWhiteSpace(query.Genre);
        bool hasStatus = !string.IsNullOrWhiteSpace(query.Status);
        int yearMin = query.YearMin ?? 0;
        int yearMax = query.YearMax ?? 0;
        if (!hasGenre && !hasStatus && yearMin <= 0 && yearMax <= 0)
        {
            return;
        }
        StartGroup(writer, SmartMatch.All);
        if (hasGenre)
        {
            WriteRule(writer, "genre", "contains", w => w.WriteStringValue(query.Genre!.Trim()));
        }
        if (hasStatus)
        {
            WriteRule(writer, "status", "is", w => w.WriteStringValue(query.Status!.Trim()));
        }
        if (yearMin > 0 && yearMax > 0)
        {
            WriteRule(writer, "year", "between", w =>
            {
                w.WriteStartArray();
                w.WriteNumberValue(yearMin);
                w.WriteNumberValue(yearMax);
                w.WriteEndArray();
            });
        }
        else if (yearMin > 0)
        {
            WriteRule(writer, "year", "gte", w => w.WriteNumberValue(yearMin));
        }
        else if (yearMax > 0)
        {
            WriteRule(writer, "year", "lte", w => w.WriteNumberValue(yearMax));
        }
        EndGroup(writer);
    }

    // One rating must match; several are alternatives.
    private static void WriteRatings(Utf8JsonWriter writer, IReadOnlyList<string> ratings)
    {
        string[] given = [.. ratings.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal)];
        if (given.Length == 0)
        {
            return;
        }
        StartGroup(writer, given.Length == 1 ? SmartMatch.All : SmartMatch.Any);
        foreach (string rating in given)
        {
            WriteRule(writer, "content_rating", "is", w => w.WriteStringValue(rating));
        }
        EndGroup(writer);
    }

    private static void StartGroup(Utf8JsonWriter writer, string match)
    {
        writer.WriteStartObject();
        writer.WriteString("match", SmartMatch.Of(match));
        writer.WriteStartArray("rules");
    }

    private static void EndGroup(Utf8JsonWriter writer)
    {
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRule(Utf8JsonWriter writer, string field, string op, Action<Utf8JsonWriter> value)
    {
        writer.WriteStartObject();
        writer.WriteString("field", field);
        writer.WriteString("op", op);
        writer.WritePropertyName("value");
        value(writer);
        writer.WriteEndObject();
    }
}

/// <summary>
/// The body of <c>POST /api/v2/catalog/query</c>: every member of the query but the image parameters, which stay in the
/// query string; the sort as a field and an order. The server refuses any member it does not know.
/// </summary>
internal static class CatalogQueryBody
{
    public static JsonElement For(CatalogQuery query) => CatalogRuleWriter.Body(writer =>
    {
        CatalogRuleWriter.WriteString(writer, "source", query.Source);
        CatalogRuleWriter.WriteString(writer, "scope", query.Scope);
        CatalogRuleWriter.WriteString(writer, "section_id", query.SectionId);
        CatalogRuleWriter.WriteString(writer, "library_id", query.LibraryId);
        CatalogRuleWriter.WriteString(writer, "collection_id", query.CollectionId);
        CatalogRuleWriter.WriteString(writer, "person_id", query.PersonId);
        CatalogRuleWriter.WriteString(writer, "q", query.Q);
        CatalogRuleWriter.WriteString(writer, "name_prefix", query.NamePrefix);
        CatalogRuleWriter.WriteString(writer, "type", query.Type);
        CatalogRuleWriter.WriteString(writer, "group", query.Group);
        CatalogRuleWriter.WriteGroups(writer, query);
        if (query.Sort is { } sort)
        {
            writer.WriteString("sort", sort.Field);
            writer.WriteString("order", sort.IsDescending ? "desc" : "asc");
        }
        if (query.Limit is { } limit)
        {
            writer.WriteNumber("limit", limit);
        }
        CatalogRuleWriter.WriteString(writer, "cursor", query.Cursor);
        if (query.Seek is { } seek)
        {
            writer.WriteNumber("seek", seek);
        }
        if (query.SkipTotal)
        {
            writer.WriteBoolean("skip_total", true);
        }
    });
}
