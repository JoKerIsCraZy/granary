using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;

namespace Granary.Core.Downloads;

/// <summary>
/// Reads what a card's Download offers, as its dialog opens: a card carries no files, so the title is read as its page reads it
/// (<c>GET /api/v2/catalog/items/{id}</c>), a season with its episodes (<c>GET /api/v2/catalog/items/{id}/episodes</c>), a
/// series with its seasons and each one's episodes (<c>GET /api/v2/catalog/series/{id}/seasons</c>, then
/// <c>.../seasons/{number}/episodes</c>), and offered as the page would offer it (<see cref="DownloadOffers"/>). The menu itself
/// reads nothing. A series' title page reads its Download series the same way: the page lists one season's episodes at most.
/// </summary>
public sealed class DownloadOfferReader(CatalogApi catalog, ISessionManager session, DownloadManager downloads)
{
    /// <summary>
    /// The offer for the film, episode, season or series <paramref name="contentId"/> of <paramref name="type"/> on the active
    /// server. Throws what the reads throw; without a server, <see cref="InvalidOperationException"/>; and
    /// <see cref="ProfileChangedException"/> when another server, account or profile became the active one while they ran:
    /// their answer is the last one's, not this one's.
    /// </summary>
    public async Task<DownloadOffer> ReadAsync(string contentId, string type, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentId);
        var scope = DownloadScope.Of(session);
        var server = session.ActiveServer?.BaseUri ?? throw new InvalidOperationException("No server is signed in to.");
        if (type == CatalogItemTypes.Season)
        {
            var (season, episodes) = await ReadSeasonAsync(server, contentId, cancellationToken).ConfigureAwait(false);
            StillFor(scope);
            return SeasonOffer(season, episodes);
        }
        if (type == CatalogItemTypes.Series)
        {
            var (series, seasons) = await ReadSeriesAsync(server, contentId, cancellationToken).ConfigureAwait(false);
            StillFor(scope);
            return SeriesOffer(series, contentId, seasons);
        }
        var item = await catalog.GetItemAsync(server, contentId, cancellationToken: cancellationToken).ConfigureAwait(false);
        StillFor(scope);
        return (DownloadOffer?)DownloadOffers.ForTitle(item, contentId) ?? new NoDownloadOffer(DownloadOffers.NoFiles);
    }

    // The season and its episodes side by side: the season names its series and number, the episodes their files.
    private async Task<(CatalogItemDetail Season, IReadOnlyList<Episode> Episodes)> ReadSeasonAsync(Uri server, string seasonId,
        CancellationToken cancellationToken)
    {
        var seasonRead = catalog.GetItemAsync(server, seasonId, cancellationToken: cancellationToken);
        var episodesRead = catalog.ListSeasonEpisodesAsync(server, seasonId, cancellationToken: cancellationToken);
        await Task.WhenAll(seasonRead, episodesRead).ConfigureAwait(false);
        return (await seasonRead.ConfigureAwait(false), (await episodesRead.ConfigureAwait(false)).Items);
    }

    // The series and its seasons side by side, then each season's episodes, one season at a time: a long series asks the server
    // for one list after another, never for all of them at once. A read the dialog's close cancelled stops between two.
    private async Task<(CatalogItemDetail Series, IReadOnlyList<(Season Season, IReadOnlyList<Episode> Episodes)> Seasons)> ReadSeriesAsync(
        Uri server, string seriesId, CancellationToken cancellationToken)
    {
        var seriesRead = catalog.GetItemAsync(server, seriesId, cancellationToken: cancellationToken);
        var seasonsRead = catalog.ListSeasonsAsync(server, seriesId, cancellationToken: cancellationToken);
        await Task.WhenAll(seriesRead, seasonsRead).ConfigureAwait(false);
        var seasons = new List<(Season, IReadOnlyList<Episode>)>();
        foreach (var season in (await seasonsRead.ConfigureAwait(false)).Items)
        {
            var episodes = await catalog.ListEpisodesAsync(server, seriesId, season.SeasonNumber, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            seasons.Add((season, episodes.Items));
        }
        return (await seriesRead.ConfigureAwait(false), seasons);
    }

    // Every season's episodes with a file that are not on this PC yet, or why there are none.
    private DownloadOffer SeriesOffer(CatalogItemDetail series, string seriesId, IReadOnlyList<(Season Season, IReadOnlyList<Episode> Episodes)> seasons)
    {
        if (!seasons.SelectMany(s => s.Episodes).Any(e => e.ContentId.Length > 0 && DownloadOffers.UsableFiles(e).Count > 0))
        {
            return new NoDownloadOffer(DownloadOffers.NoFiles);
        }
        return (DownloadOffer?)DownloadOffers.ForSeries(series, seriesId, seasons, IsOnItsWay)
            ?? new NoDownloadOffer(DownloadOffers.SeriesOnThisPc);
    }

    // The season's episodes with a file that are not on this PC yet, or why there are none.
    private DownloadOffer SeasonOffer(CatalogItemDetail season, IReadOnlyList<Episode> episodes)
    {
        if (!episodes.Any(e => e.ContentId.Length > 0 && DownloadOffers.UsableFiles(e).Count > 0))
        {
            return new NoDownloadOffer(DownloadOffers.NoFiles);
        }
        return (DownloadOffer?)DownloadOffers.ForSeason(season, episodes, IsOnItsWay)
            ?? new NoDownloadOffer(season.SeriesId is { Length: > 0 } && season.SeasonNumber is not null ? DownloadOffers.SeasonOnThisPc : DownloadOffers.NoFiles);
    }

    // On this PC, coming, or waiting for the server's limit of downloads: not offered again.
    private bool IsOnItsWay(string mediaItemId) => downloads.ForItem(mediaItemId) is not null || downloads.IsWaiting(mediaItemId);

    // An answer that came after another server, account or profile became the active one is dropped.
    private void StillFor(DownloadScope? scope)
    {
        if (DownloadScope.Of(session) != scope)
        {
            throw new ProfileChangedException("Another server, account or profile became the active one while the title was read.");
        }
    }
}
