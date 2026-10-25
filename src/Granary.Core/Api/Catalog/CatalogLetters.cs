using System.Text.Json;

namespace Granary.Core.Api.Catalog;

/// <summary>
/// <c>POST /api/v2/catalog/letters</c>: how many titles of a title-ordered listing file under each letter, and the list
/// position of each letter's first title, for the A–Z bar (docs/api-contract.md, 7.4).
/// </summary>
public sealed record CatalogLetters
{
    /// <summary>Titles in the whole result: the sum of the counts.</summary>
    public int Total { get; init; }

    /// <summary>Letters with at least one title, in list order.</summary>
    public IReadOnlyList<CatalogLetter> Letters { get; init => field = value ?? []; } = [];

    /// <summary>
    /// Blocks of consecutive titles under one letter, covering the whole result in list order. A collation can sort
    /// some of a letter's titles apart from the rest, so these, not <see cref="Letters"/>, tell which letter a list
    /// position is under.
    /// </summary>
    public IReadOnlyList<CatalogLetterRun> Runs { get; init => field = value ?? []; } = [];

    /// <summary>The letter whose titles the list shows at <paramref name="index"/>: that of the last run starting at or before it.</summary>
    public string? LetterAt(int index)
    {
        string? current = null;
        foreach (var run in Runs)
        {
            if (run.Position > index)
            {
                break;
            }
            current = run.Letter;
        }
        return current;
    }
}

/// <param name="Letter">"A" to "Z", or "#" for titles starting with a digit, a symbol or a non-Latin letter.</param>
/// <param name="Position">Zero-based list position of the letter's first title: a <see cref="CatalogQuery.Seek"/>.</param>
public sealed record CatalogLetter(string Letter, int Count, int Position);

public sealed record CatalogLetterRun(string Letter, int Position);

/// <summary>The body of the letter index of a listing: its scope and filters as rule groups, in its title order.</summary>
internal static class CatalogLettersBody
{
    /// <summary>
    /// The filters become the rule groups the server makes of them on <c>GET /api/v2/catalog</c>, so the positions
    /// count the same titles the pages list.
    /// </summary>
    public static JsonElement For(CatalogQuery query)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(query.LibraryId))
            {
                writer.WriteString("library_id", query.LibraryId);
            }
            if (!string.IsNullOrWhiteSpace(query.Type))
            {
                writer.WriteString("type", query.Type);
            }
            writer.WriteStartArray("groups");
            WriteFilters(writer, query);
            WriteRatings(writer, query.ContentRatings ?? []);
            writer.WriteEndArray();
            writer.WriteString("order", query.Sort is { IsDescending: true } ? "desc" : "asc");
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
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
        StartGroup(writer, "all");
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
        StartGroup(writer, given.Length == 1 ? "all" : "any");
        foreach (string rating in given)
        {
            WriteRule(writer, "content_rating", "is", w => w.WriteStringValue(rating));
        }
        EndGroup(writer);
    }

    private static void StartGroup(Utf8JsonWriter writer, string match)
    {
        writer.WriteStartObject();
        writer.WriteString("match", match);
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
