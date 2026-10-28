using System.Globalization;
using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels;

/// <summary>A person's facts as the web's person page badges them (<c>pages/PersonDetail.tsx</c>, <c>lib/date.ts</c>).</summary>
public static class PersonFacts
{
    /// <summary>"Born Apr 25, 1940", "86 years old" or "Died Feb 28, 2005 (age 84)", and the birthplace.</summary>
    public static IReadOnlyList<string> Of(Person person, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(person);
        var facts = new List<string>();
        var born = Date(person.BirthDate);
        var died = Date(person.DeathDate);
        if (NonEmpty(person.BirthDate) is { } birth)
        {
            facts.Add($"Born {Format(born, birth)}");
            if (NonEmpty(person.DeathDate) is null && born is { } birthday)
            {
                facts.Add($"{Age(birthday, today)} years old");
            }
        }
        if (NonEmpty(person.DeathDate) is { } death && NonEmpty(person.BirthDate) is not null)
        {
            facts.Add(born is { } birthday && died is { } deathday
                ? $"Died {Format(died, death)} (age {Age(birthday, deathday)})"
                : $"Died {Format(died, death)}");
        }
        if (NonEmpty(person.Birthplace) is { } place)
        {
            facts.Add(place);
        }
        return facts;
    }

    /// <summary>Full years from <paramref name="birth"/> to <paramref name="on"/>.</summary>
    public static int Age(DateOnly birth, DateOnly on)
    {
        int age = on.Year - birth.Year;
        return on.Month < birth.Month || (on.Month == birth.Month && on.Day < birth.Day) ? age - 1 : age;
    }

    // The server keeps calendar dates as YYYY-MM-DD; one it does not shows as it came, as the web's does.
    private static DateOnly? Date(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static string Format(DateOnly? date, string raw) =>
        date is { } day ? day.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : raw.Trim();

    private static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
