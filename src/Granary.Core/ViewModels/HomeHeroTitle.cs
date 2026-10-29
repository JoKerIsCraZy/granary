using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels;

/// <summary>
/// What the top of Home shows of a title (docs/ui-design.md, Home): its logo, or its name without one, a line of details,
/// the overview and the backdrop behind them. An episode is headed by its series.
/// </summary>
/// <param name="Key">The title's content id: the same title loaded again is the same hero.</param>
/// <param name="Meta">"2008 · 2h 32m · PG-13", an episode's "S1 E1 · Kein Zufall · 25m · TV-14"; empty when nothing is known.</param>
public sealed record HomeHeroTitle(string Key, string Heading, string Meta, string? Overview, Uri? LogoUrl, Uri? BackdropUrl, string? BackdropThumbhash)
{
    public static HomeHeroTitle From(CatalogItemBase item, Uri server)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool isEpisode = item.Type == CatalogItemTypes.Episode;
        string heading = isEpisode && !string.IsNullOrWhiteSpace(item.SeriesTitle) ? item.SeriesTitle : item.Title;
        return new HomeHeroTitle(
            item.ContentId,
            heading,
            MetaOf(item),
            string.IsNullOrWhiteSpace(item.Overview) ? null : item.Overview.Trim(),
            ArtworkUrl.Resolve(server, item.LogoUrl),
            ArtworkUrl.Resolve(server, item.BackdropUrl),
            item.BackdropThumbhash);
    }

    // A movie its year, length and rating; a series its year and rating (its length is an episode's); an episode where it
    // sits in its series, its own title, its length and rating.
    private static string MetaOf(CatalogItemBase item)
    {
        string? rating = item.ContentRating;
        string?[] parts = item.Type switch
        {
            CatalogItemTypes.Episode => [EpisodeCode(item.SeasonNumber, item.EpisodeNumber), item.Title, DetailText.Runtime(item.Runtime), rating],
            CatalogItemTypes.Movie => [DetailText.Year(item.Year), DetailText.Runtime(item.Runtime), rating],
            _ => [DetailText.Year(item.Year), rating],
        };
        return string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
    }

    private static string? EpisodeCode(int? season, int? episode) =>
        season is int s && episode is int e ? string.Create(CultureInfo.InvariantCulture, $"S{s} E{e}") : null;
}
