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
    public static JsonElement For(CatalogQuery query) => CatalogRuleWriter.Body(writer =>
    {
        CatalogRuleWriter.WriteString(writer, "library_id", query.LibraryId);
        CatalogRuleWriter.WriteString(writer, "type", query.Type);
        CatalogRuleWriter.WriteGroups(writer, query);
        writer.WriteString("order", query.Sort is { IsDescending: true } ? "desc" : "asc");
    });
}
