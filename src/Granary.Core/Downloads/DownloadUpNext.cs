using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.Downloads;

/// <summary>
/// The episodes around the playing one with downloads in mind (docs/architecture.md, "Playing a download"): the server's
/// answer comes first, so a series goes on in its order, and the next episode plays from this PC when it is downloaded too
/// (<see cref="DownloadPlaybacks"/>). When the server has none to name, as without a connection, the active profile's
/// downloads of the series answer: the next one on this PC, as Silo's Android app goes on to an episode it holds. On Deck is
/// the server's.
/// </summary>
public sealed class DownloadUpNext(UpNextSource server, DownloadStore store, ISessionManager session, TimeProvider time) : IUpNextSource
{
    /// <summary>
    /// How long the server may take before the downloads answer in its place: the next-up card comes with the credits, and a
    /// connection that hangs would hold it past them.
    /// </summary>
    public static readonly TimeSpan ServerBudget = TimeSpan.FromSeconds(5);

    public async Task<NextEpisode?> NextEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        return await FromServerAsync(server.NextEpisodeAsync(current, cancellationToken), cancellationToken).ConfigureAwait(false)
            ?? Downloaded(current, e => e.Season > current.SeasonNumber || (e.Season == current.SeasonNumber && e.Episode > current.EpisodeNumber))
                .OrderBy(e => e.Season).ThenBy(e => e.Episode).Select(e => e.Next).FirstOrDefault();
    }

    // The web player's previous-episode button stays inside the season; so does this.
    public async Task<NextEpisode?> PreviousEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        return await FromServerAsync(server.PreviousEpisodeAsync(current, cancellationToken), cancellationToken).ConfigureAwait(false)
            ?? Downloaded(current, e => e.Season == current.SeasonNumber && e.Episode < current.EpisodeNumber)
                .OrderByDescending(e => e.Episode).Select(e => e.Next).FirstOrDefault();
    }

    public Task<IReadOnlyList<MediaCard>> OnDeckAsync(string? exceptSeriesId, CancellationToken cancellationToken) =>
        server.OnDeckAsync(exceptSeriesId, cancellationToken);

    // The server's answer, or none once the budget is over: the server's source answers null by itself when it cannot say.
    private async Task<NextEpisode?> FromServerAsync(Task<NextEpisode?> answer, CancellationToken cancellationToken)
    {
        try
        {
            return await answer.WaitAsync(ServerBudget, time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // It may still fail later; nobody waits for it any more.
            _ = answer.ContinueWith(static late => _ = late.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return null;
        }
    }

    // The active profile's downloads of the series that can play and say where they stand in it, without the specials
    // unless the playing episode is one.
    private IEnumerable<(int Season, int Episode, NextEpisode Next)> Downloaded(PlayingEpisode current, Func<(int Season, int Episode), bool> wanted)
    {
        if (DownloadScope.Of(session) is not { } scope)
        {
            return [];
        }
        return store.In(scope)
            .Where(d => d is { Record.State: DownloadState.Completed, FilesMissing: false, MediaPath: not null } && d.Record.SeriesId == current.SeriesId
                && d.Record.MediaItemId != current.ContentId)
            .Select(d => d.Record)
            .Where(r => r is { SeasonNumber: { } season, EpisodeNumber: not null } && (season > 0 || season == current.SeasonNumber))
            .Select(r => (Season: r.SeasonNumber!.Value, Episode: r.EpisodeNumber!.Value,
                Next: new NextEpisode(r.MediaItemId, r.SeasonNumber.Value, r.EpisodeNumber.Value, r.Title)))
            .Where(e => wanted((e.Season, e.Episode)));
    }
}
