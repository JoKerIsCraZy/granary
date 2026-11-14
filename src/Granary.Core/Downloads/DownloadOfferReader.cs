using Granary.Core.Api;
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
    /// Throws what the reads throw; without a server, <see cref="InvalidOperationException"/>; and
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

    // The season's episodes with a file that are not on this PC yet, or why there are none.
    private DownloadOffer SeasonOffer(CatalogItemDetail season, IReadOnlyList<Episode> episodes)
    {
        if (!episodes.Any(e => e.ContentId.Length > 0 && DownloadOffers.UsableFiles(e).Count > 0))
        {
            return new NoDownloadOffer(DownloadOffers.NoFiles);
        }
        return (DownloadOffer?)DownloadOffers.ForSeason(season, episodes, id => downloads.ForItem(id) is not null)
            ?? new NoDownloadOffer(season.SeriesId is { Length: > 0 } && season.SeasonNumber is not null ? DownloadOffers.SeasonOnThisPc : DownloadOffers.NoFiles);
    }

    // An answer that came after another server, account or profile became the active one is dropped.
    private void StillFor(DownloadScope? scope)
    {
        if (DownloadScope.Of(session) != scope)
        {
            throw new ProfileChangedException("Another server, account or profile became the active one while the title was read.");
        }
    }
}
