using Granary.Core.Api.Catalog;
using Granary.Core.Auth;

namespace Granary.Core.Downloads;

/// <summary>
/// Reads what a card's Download offers, as its dialog opens: a card carries no files, so the title is read as its page reads it
/// (<c>GET /api/v2/catalog/items/{id}</c>), a season with its episodes (<c>GET /api/v2/catalog/items/{id}/episodes</c>), and
/// offered as the page would offer it (<see cref="DownloadOffers"/>). The menu itself reads nothing.
/// </summary>
public sealed class DownloadOfferReader(CatalogApi catalog, ISessionManager session, DownloadManager downloads)
{
    /// <summary>
    /// The offer for the film, episode or season <paramref name="contentId"/> of <paramref name="type"/> on the active server.
    /// Throws what the reads throw; without a server, <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<DownloadOffer> ReadAsync(string contentId, string type, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentId);
        var server = session.ActiveServer?.BaseUri ?? throw new InvalidOperationException("No server is signed in to.");
        if (type == CatalogItemTypes.Season)
        {
            return await ReadSeasonAsync(server, contentId, cancellationToken).ConfigureAwait(false);
        }
        var item = await catalog.GetItemAsync(server, contentId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (DownloadOffer?)DownloadOffers.ForTitle(item, contentId) ?? new NoDownloadOffer(DownloadOffers.NoFiles);
    }

    // The season and its episodes side by side: the season names its series and number, the episodes their files.
    private async Task<DownloadOffer> ReadSeasonAsync(Uri server, string seasonId, CancellationToken cancellationToken)
    {
        var seasonRead = catalog.GetItemAsync(server, seasonId, cancellationToken: cancellationToken);
        var episodesRead = catalog.ListSeasonEpisodesAsync(server, seasonId, cancellationToken: cancellationToken);
        await Task.WhenAll(seasonRead, episodesRead).ConfigureAwait(false);
        var season = await seasonRead.ConfigureAwait(false);
        var episodes = (await episodesRead.ConfigureAwait(false)).Items;
        if (!episodes.Any(e => e.ContentId.Length > 0 && DownloadOffers.UsableFiles(e).Count > 0))
        {
            return new NoDownloadOffer(DownloadOffers.NoFiles);
        }
        return (DownloadOffer?)DownloadOffers.ForSeason(season, episodes, id => downloads.ForItem(id) is not null)
            ?? new NoDownloadOffer(season.SeriesId is { Length: > 0 } && season.SeasonNumber is not null ? DownloadOffers.SeasonOnThisPc : DownloadOffers.NoFiles);
    }
}
