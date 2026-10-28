using System.Globalization;
using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>How the web's room names a title (<c>room/stage/StagedHero.tsx</c>).</summary>
public static class WatchPartyText
{
    /// <summary>"1h 36m", "45m"; empty without a length.</summary>
    public static string Runtime(int? minutes) => minutes is int m && m > 0
        ? m >= 60 ? $"{m / 60}h {m % 60}m" : $"{m}m"
        : "";

    /// <summary>"Evan Allmächtig (2007)", or an episode's "Show — S1 E2 \"Title\"".</summary>
    public static string Headline(CatalogItemBase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Type == CatalogItemTypes.Episode && item.SeriesTitle is { Length: > 0 } series)
        {
            string code = $"S{Number(item.SeasonNumber)} E{Number(item.EpisodeNumber)}";
            return item.Title.Length > 0 ? $"{series} — {code} “{item.Title}”" : $"{series} — {code}";
        }
        return item.Year is { } year ? $"{item.Title} ({year.ToString(CultureInfo.InvariantCulture)})" : item.Title;
    }

    /// <summary>"1h 36m · PG · Fantasy, Komödie": a movie's first two genres, none for a series or an episode.</summary>
    public static string Meta(CatalogItemBase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var parts = new List<string>();
        if (Runtime(item.Runtime) is { Length: > 0 } runtime)
        {
            parts.Add(runtime);
        }
        if (item.ContentRating is { Length: > 0 } rating)
        {
            parts.Add(rating);
        }
        if (item.Type == CatalogItemTypes.Movie && item.Genres.Count > 0)
        {
            parts.Add(string.Join(", ", item.Genres.Take(2)));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Parts joined with " · ", the empty ones left out.</summary>
    public static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "?";
}
