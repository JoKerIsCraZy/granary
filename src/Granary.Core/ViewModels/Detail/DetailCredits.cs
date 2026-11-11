using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// The "Cast &amp; Crew" row, in groups built from the crew jobs like the web client's (web/src/components/castCrewGroups.ts
/// and CastCarousel.tsx).
/// </summary>
public static class DetailCredits
{
    private const string Director = "Director";
    private const string Creator = "Creator";
    private const string Writers = "Writers";
    private const string Cast = "Cast";
    private const int DirectorCards = 2;
    private const int WriterCards = 3;

    /// <summary>
    /// The director (two at most) or every creator of a series, then up to three writers who are not already shown, then
    /// the cast in billing order. A season shows its cast only. Groups without cards are left out, and the first group
    /// that is left has no label.
    /// </summary>
    public static IReadOnlyList<DetailCreditGroup> Groups(CatalogItemDetail item, Uri serverBase)
    {
        ArgumentNullException.ThrowIfNull(item);
        var groups = new List<(string Label, IReadOnlyList<DetailPerson> People)>();
        if (item.Type != CatalogItemTypes.Season)
        {
            var (leadJob, leadCredits) = Lead(item);
            string leadRole = leadJob ?? Director;
            var lead = CrewCards(leadCredits, leadRole, leadRole == Creator ? int.MaxValue : DirectorCards, serverBase);
            // A writer-director already has a card. Only the lead cards shown count: a third director still shows as a writer.
            var shown = lead.Select(p => Key(p.PersonId, p.Name)).ToHashSet(StringComparer.Ordinal);
            var writers = item.Crew.Where(c => c is not null && IsWriter(c) && !shown.Contains(Key(c.PersonId, c.Name)));
            groups.Add((leadRole, lead));
            groups.Add((Writers, CrewCards([.. writers], "Writer", WriterCards, serverBase)));
        }
        int castLimit = item.Type is CatalogItemTypes.Movie or CatalogItemTypes.Series ? 12 : 20;
        // Not made unique, as the web's: an actor with two characters has a card for each.
        groups.Add((Cast, [.. item.Cast
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Name))
            .OrderBy(c => c.Order)
            .Take(castLimit)
            .Select(c => new DetailPerson(c.Name.Trim(), Blank(c.Character), ArtworkUrl.Resolve(serverBase, c.PhotoUrl), c.PersonId))]));
        return [.. groups
            .Where(g => g.People.Count > 0)
            .Select((g, i) => new DetailCreditGroup(i == 0 ? null : g.Label, g.People))];
    }

    /// <summary>The row as laid out: each group's cards, after a separator for every group with a label.</summary>
    public static IReadOnlyList<DetailCredit> Row(IReadOnlyList<DetailCreditGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var row = new List<DetailCredit>();
        foreach (var group in groups)
        {
            if (group.Label is not null)
            {
                row.Add(new DetailCreditSeparator(group.Label));
            }
            row.AddRange(group.People);
        }
        return row;
    }

    /// <summary>"Cast &amp; Crew" when the row has crew cards, "Cast" otherwise.</summary>
    public static string PeopleTitle(IReadOnlyList<DetailPerson> people) => people.Any(p => p.IsCrew) ? "Cast & Crew" : "Cast";

    // Each person once, up to max cards, captioned with the group's role.
    private static List<DetailPerson> CrewCards(IReadOnlyList<CrewCredit> members, string role, int max, Uri serverBase)
    {
        var cards = new List<DetailPerson>();
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members.Where(m => !string.IsNullOrWhiteSpace(m.Name)))
        {
            if (cards.Count == max)
            {
                break;
            }
            if (added.Add(Key(member.PersonId, member.Name)))
            {
                cards.Add(new DetailPerson(member.Name.Trim(), role, ArtworkUrl.Resolve(serverBase, member.PhotoUrl), member.PersonId, IsCrew: true));
            }
        }
        return cards;
    }

    // A series leads with its creators and falls back to its directors.
    private static (string? Job, IReadOnlyList<CrewCredit> Members) Lead(CatalogItemDetail item)
    {
        string[] jobs = item.Type == CatalogItemTypes.Series ? [Creator, Director] : [Director];
        string? job = jobs.FirstOrDefault(j => item.Crew.Any(c => c?.Job == j));
        return (job, job is null ? [] : [.. item.Crew.Where(c => c?.Job == job)]);
    }

    private static bool IsWriter(CrewCredit? credit) => credit?.Job is "Writer" or "Screenplay";

    // Names and ids may be missing however the schema reads: nothing here may throw on them.
    private static string Key(string? personId, string? name) => personId is { Length: > 0 } id ? id : name?.Trim() ?? "";

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
