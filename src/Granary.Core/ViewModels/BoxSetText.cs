using System.Globalization;
using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels;

/// <summary>A box set's words, as the web's <c>lib/boxSets.ts</c> writes them, and the film its Play starts.</summary>
public static class BoxSetText
{
    /// <summary>"8 films", or "1 film".</summary>
    public static string FilmCount(int count) => count == 1 ? "1 film" : string.Create(CultureInfo.InvariantCulture, $"{count} films");

    /// <summary>"2001 – 2011", one year when the box set spans one; null without a year.</summary>
    public static string? Years(CatalogBoxSetBase boxSet)
    {
        ArgumentNullException.ThrowIfNull(boxSet);
        int? first = boxSet.FirstYear is > 0 ? boxSet.FirstYear : null;
        int? last = boxSet.LastYear is > 0 ? boxSet.LastYear : null;
        if (first is { } from && last is { } to && from != to)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{from} – {to}");
        }
        return (first ?? last)?.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The box set page's facts: the films, the years, and how many are watched once one is.</summary>
    public static IReadOnlyList<string> Facts(CatalogBoxSetBase boxSet)
    {
        ArgumentNullException.ThrowIfNull(boxSet);
        return [.. new[]
        {
            FilmCount(boxSet.MovieCount),
            Years(boxSet),
            boxSet.PlayedCount > 0 ? string.Create(CultureInfo.InvariantCulture, $"{boxSet.PlayedCount} of {boxSet.MovieCount} watched") : null,
        }.OfType<string>()];
    }

    /// <summary>
    /// The film Play starts: the first in release order the profile has not played (one in progress is not played), or the
    /// first once all are; null without films. As the web's and the apps' <c>boxSetNextMovie</c>.
    /// </summary>
    public static CatalogItem? NextFilm(IReadOnlyList<CatalogItem> films)
    {
        ArgumentNullException.ThrowIfNull(films);
        return films.FirstOrDefault(film => film.UserState?.Played != true) ?? films.FirstOrDefault();
    }

    /// <summary>
    /// "Play Groundwork", or "Continue · Masterplan" once some films are played and not all, or the next one is started, as
    /// Android's <c>playLabel</c> (the web and the Apple app look at the played count only).
    /// </summary>
    public static string PlayLabel(CatalogBoxSetBase boxSet, CatalogItem next)
    {
        ArgumentNullException.ThrowIfNull(boxSet);
        ArgumentNullException.ThrowIfNull(next);
        bool started = boxSet.PlayedCount > 0 && boxSet.PlayedCount < boxSet.MovieCount;
        bool resumes = next.PositionSeconds is > 0 && next.UserState?.Played != true;
        return started || resumes ? $"Continue · {next.Title}" : $"Play {next.Title}";
    }
}
