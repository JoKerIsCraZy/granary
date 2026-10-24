using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// The credits line under the overview and the "Cast &amp; Crew" row, built from the crew jobs like the web
/// client (web/src/pages/ItemDetail/components/HeroCrewLine.tsx, web/src/components/castCrewGroups.ts).
/// </summary>
public static class DetailCredits
{
    private const string Director = "Director";
    private const string Creator = "Creator";
    private const int LeadNamesInLine = 2;
    private const int WriterNamesInLine = 2;
    private const int DirectorCards = 2;
    private const int WriterCards = 3;

    /// <summary>"Directed by …", "Written by …" and the genres; a series leads with "Created by …".</summary>
    public static IReadOnlyList<DetailCreditPart> Line(CatalogItemDetail item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Type == CatalogItemTypes.Season)
        {
            return [];
        }
        var parts = new List<DetailCreditPart>();
        var (leadJob, lead) = Lead(item);
        string[] leadNames = Names(lead, LeadNamesInLine);
        if (leadNames.Length > 0)
        {
            parts.Add(new DetailCreditPart(leadJob == Creator ? "Created by" : "Directed by", string.Join(", ", leadNames)));
        }
        string[] writers = Names(item.Crew.Where(IsWriter), WriterNamesInLine);
        if (writers.Length > 0)
        {
            parts.Add(new DetailCreditPart("Written by", string.Join(", ", writers)));
        }
        // An episode's genres are its series'; the web leaves them out there.
        if (item.Type != CatalogItemTypes.Episode)
        {
            parts.AddRange(item.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => new DetailCreditPart(null, g.Trim())));
        }
        return parts;
    }

    /// <summary>
    /// The director (two at most) or every creator of a series, then up to three writers who are not already
    /// shown, then the cast in billing order. A season shows its cast only.
    /// </summary>
    public static IReadOnlyList<DetailPerson> People(CatalogItemDetail item, Uri serverBase)
    {
        ArgumentNullException.ThrowIfNull(item);
        var people = new List<DetailPerson>();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        if (item.Type != CatalogItemTypes.Season)
        {
            var (leadJob, lead) = Lead(item);
            AddCrew(lead, leadJob ?? Director, leadJob == Creator ? int.MaxValue : DirectorCards);
            AddCrew([.. item.Crew.Where(c => c is not null && IsWriter(c) && !shown.Contains(Key(c)))], "Writer", WriterCards);
        }
        int castLimit = item.Type is CatalogItemTypes.Movie or CatalogItemTypes.Series ? 12 : 20;
        people.AddRange(item.Cast
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Name))
            .OrderBy(c => c.Order)
            .Take(castLimit)
            .Select(c => new DetailPerson(c.Name.Trim(), Blank(c.Character), ArtworkUrl.Resolve(serverBase, c.PhotoUrl), c.PersonId)));
        return people;

        void AddCrew(IReadOnlyList<CrewCredit> members, string role, int max)
        {
            var added = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members.Where(m => !string.IsNullOrWhiteSpace(m.Name)))
            {
                if (added.Count == max)
                {
                    break;
                }
                if (added.Add(Key(member)))
                {
                    shown.Add(Key(member));
                    people.Add(new DetailPerson(member.Name.Trim(), role, ArtworkUrl.Resolve(serverBase, member.PhotoUrl), member.PersonId, IsCrew: true));
                }
            }
        }
    }

    /// <summary>"Cast &amp; Crew" when the row has crew cards, "Cast" otherwise.</summary>
    public static string PeopleTitle(IReadOnlyList<DetailPerson> people) => people.Any(p => p.IsCrew) ? "Cast & Crew" : "Cast";

    // A series leads with its creators and falls back to its directors.
    private static (string? Job, IReadOnlyList<CrewCredit> Members) Lead(CatalogItemDetail item)
    {
        string[] jobs = item.Type == CatalogItemTypes.Series ? [Creator, Director] : [Director];
        string? job = jobs.FirstOrDefault(j => item.Crew.Any(c => c?.Job == j));
        return (job, job is null ? [] : [.. item.Crew.Where(c => c?.Job == job)]);
    }

    private static bool IsWriter(CrewCredit? credit) => credit?.Job is "Writer" or "Screenplay";

    private static string[] Names(IEnumerable<CrewCredit> credits, int max) =>
        [.. credits.Select(c => c?.Name?.Trim()).OfType<string>().Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Take(max)];

    // Names and ids may be missing however the schema reads: nothing here may throw on them.
    private static string Key(CrewCredit credit) => credit.PersonId is { Length: > 0 } id ? id : credit.Name?.Trim() ?? "";

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
