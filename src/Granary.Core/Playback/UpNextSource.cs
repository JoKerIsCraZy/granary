using Granary.Core.Auth;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>What the player offers after an episode: the next one, and the profile's other titles in progress.</summary>
public interface IUpNextSource
{
    /// <summary>The episode after <paramref name="current"/>; null at the end of the series or when the server cannot say.</summary>
    Task<NextEpisode?> NextEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken);

    /// <summary>The episode before <paramref name="current"/>; null at the start of the series or when the server cannot say.</summary>
    Task<NextEpisode?> PreviousEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken);

    /// <summary>The profile's Continue Watching cards ("On Deck"), without those of <paramref name="exceptSeriesId"/>.</summary>
    Task<IReadOnlyList<MediaCard>> OnDeckAsync(string? exceptSeriesId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the next episode as Android and Apple resolve it (docs/api-contract.md, 10.7): the current season's episodes,
/// and when it has none later, those of the lowest later season that is not specials. On Deck is the Home row
/// Continue Watching, as both apps take it.
/// </summary>
public sealed class UpNextSource(CatalogApi catalog, ISessionManager session, ILogger<UpNextSource> logger) : IUpNextSource
{
    // The season's episodes while a read of them is on its way: the next and the previous episode are asked for
    // together, and need the same list.
    private (string Key, Task<EpisodeCollection> Read)? _seasonRead;
    public async Task<NextEpisode?> NextEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (session.ActiveServer?.BaseUri is not { } server)
        {
            return null;
        }
        try
        {
            var season = await ReadSeasonAsync(server, current, cancellationToken).ConfigureAwait(false);
            var next = NextEpisode.After(season.Items, current.SeasonNumber, current.EpisodeNumber);
            if (next is null)
            {
                var seasons = await catalog.ListSeasonsAsync(server, current.SeriesId, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (seasons.Items.Where(s => s.IsSpecials != true && s.SeasonNumber > current.SeasonNumber).MinBy(s => s.SeasonNumber)
                    is { } later)
                {
                    var episodes = await catalog.ListEpisodesAsync(server, current.SeriesId, later.SeasonNumber, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    next = NextEpisode.After(episodes.Items, current.SeasonNumber, current.EpisodeNumber);
                }
            }
            return next is null ? null : NextEpisode.Of(next, server);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            logger.LogInformation("The next episode could not be read: {Failure}", ApiFailureText.Describe(ex));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nobody waits for this read: the player goes on without a next episode.
            logger.LogError(ex, "Reading the next episode failed unexpectedly");
            return null;
        }
    }

    // The web player's previous-episode button: the one before in the current season, none before its first.
    public async Task<NextEpisode?> PreviousEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (session.ActiveServer?.BaseUri is not { } server)
        {
            return null;
        }
        try
        {
            var season = await ReadSeasonAsync(server, current, cancellationToken).ConfigureAwait(false);
            var previous = NextEpisode.Before(season.Items, current.SeasonNumber, current.EpisodeNumber);
            return previous is null ? null : NextEpisode.Of(previous, server);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            logger.LogInformation("The previous episode could not be read: {Failure}", ApiFailureText.Describe(ex));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reading the previous episode failed unexpectedly");
            return null;
        }
    }

    private Task<EpisodeCollection> ReadSeasonAsync(Uri server, PlayingEpisode current, CancellationToken cancellationToken)
    {
        string key = string.Join('|', server.AbsoluteUri, current.SeriesId, current.SeasonNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_seasonRead is { } pending && pending.Key == key && !pending.Read.IsCompleted)
        {
            return pending.Read;
        }
        var read = catalog.ListEpisodesAsync(server, current.SeriesId, current.SeasonNumber, cancellationToken: cancellationToken);
        _seasonRead = (key, read);
        return read;
    }

    public async Task<IReadOnlyList<MediaCard>> OnDeckAsync(string? exceptSeriesId, CancellationToken cancellationToken)
    {
        if (session.ActiveServer?.BaseUri is not { } server)
        {
            return [];
        }
        try
        {
            var sections = await catalog.ListHomeSectionsAsync(server, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (sections.Sections.FirstOrDefault(s => s.SectionType == SectionTypes.ContinueWatching) is not { } row)
            {
                return [];
            }
            if (row.Items.Count == 0 && row.TotalCount > 0)
            {
                row = await catalog.GetHomeSectionItemsAsync(server, row.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            return
            [
                .. row.Items
                    .Where(item => HomeRowRules.Shows(row.SectionType, item) && (exceptSeriesId is null || (item.SeriesId != exceptSeriesId && item.ContentId != exceptSeriesId)))
                    .Select(item => MediaCardFactory.ForSection(row, item, server)),
            ];
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            logger.LogInformation("On Deck could not be read: {Failure}", ApiFailureText.Describe(ex));
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reading On Deck failed unexpectedly");
            return [];
        }
    }
}
