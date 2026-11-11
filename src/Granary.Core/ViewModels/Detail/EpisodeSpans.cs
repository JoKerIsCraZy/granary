using Granary.Core.Api.Catalog;
using Granary.Core.Auth;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// The episodes a listed episode's multi-episode file holds (S01E05-E06 is linked to E05 only), read from the episode's
/// versions (docs/api-contract.md 7.7) and kept for a few minutes per episode, so a season's grid never shows a double
/// episode's second half as missing. A change of server, profile or session forgets them. One for the whole app.
/// </summary>
public sealed class EpisodeSpans
{
    private readonly CatalogApi _catalog;
    private readonly KeptAnswers<(string Server, string? Profile, string Episode), IReadOnlyCollection<int>> _kept;

    public EpisodeSpans(CatalogApi catalog, ISessionManager session, TimeProvider time)
    {
        _catalog = catalog;
        _kept = new(time, SeriesAirings.KeepFor, SeriesAirings.GiveUpAfter, key => SeriesAirings.Acts(session, key.Server, key.Profile));
        session.Changed += (_, _) => _kept.Clear();
    }

    /// <summary>The numbers the episode's multi-episode files hold, its own included; none for a file of one episode.</summary>
    public Task<IReadOnlyCollection<int>> GetAsync(Uri server, string? profileId, string episodeId, CancellationToken cancellationToken) =>
        _kept.GetAsync((server.AbsoluteUri, profileId, episodeId),
            async giveUp => SeasonGaps.Spanned(await _catalog.GetItemAsync(server, episodeId, cancellationToken: giveUp)), cancellationToken);
}
