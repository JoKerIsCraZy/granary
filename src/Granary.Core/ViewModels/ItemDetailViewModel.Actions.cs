using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// Navigation and the watched, favourite and watchlist actions of the detail page. The toggles allow
// concurrent executions so their buttons never disable themselves (a keyboard user's focus would drop);
// a change already on its way makes further clicks no-ops instead.
public sealed partial class ItemDetailViewModel
{
    private bool _isChangingWatched;
    private bool _isChangingFavorite;
    private bool _isChangingWatchlist;

    [RelayCommand]
    private void OpenCard(MediaCard? card) => Open(card?.Id);

    [RelayCommand]
    private void OpenEpisode(DetailEpisode? episode) => Open(episode?.Id);

    [RelayCommand]
    private void OpenCrumb(DetailCrumb? crumb) => Open(crumb?.TargetId);

    /// <summary>A cast or crew card opens the person's page, as the web's cast row links it; one without an id stays.</summary>
    [RelayCommand]
    private void OpenPerson(DetailPerson? person)
    {
        if (person is { CanOpen: true, PersonId: { } id })
        {
            _navigation.NavigateTo(AppRoute.Person, id);
        }
    }

    [RelayCommand]
    private void GoBack() => _navigation.GoBack();

    /// <summary>Marks the item watched or unwatched; a series or season covers its episodes, and both clear the resume point.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleWatchedAsync()
    {
        if (_item is not { } item || _serverBase is not { } server || !ShowsWatchedAction || _isChangingWatched)
        {
            return;
        }
        _isChangingWatched = true;
        int generation = _generation;
        bool watched = !IsWatched;
        var before = (Watch: _watch, IsWatched, Seasons: _seasons, Episodes: _episodes);
        ActionError = "";
        _watch = Marked(_watch, watched, item.Type is CatalogItemTypes.Movie or CatalogItemTypes.Episode);
        IsWatched = watched;
        _seasons = [.. _seasons.Select(s => Marked(s, watched))];
        _episodes = [.. _episodes.Select(e => Marked(e, watched))];
        var marked = (Seasons: _seasons, Episodes: _episodes);
        ShowWatchState();
        try
        {
            await _catalog.SetWatchedAsync(server, _contentId, watched);
            _changes?.Report();
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Marking item {ItemId} {Change} failed: {Failure}", _contentId, watched ? "watched" : "unwatched", ApiFailureText.Describe(ex));
            if (generation == _generation)
            {
                _watch = before.Watch;
                IsWatched = before.IsWatched;
                // A list that arrived while the change was on its way already shows the server's state.
                _seasons = ReferenceEquals(_seasons, marked.Seasons) ? before.Seasons : _seasons;
                _episodes = ReferenceEquals(_episodes, marked.Episodes) ? before.Episodes : _episodes;
                ShowWatchState();
                ActionError = DescribeActionFailure(watched ? "mark this as watched" : "mark this as unwatched", ex);
            }
        }
        finally
        {
            _isChangingWatched = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleFavoriteAsync()
    {
        if (_serverBase is not { } server || !CanFavorite || _isChangingFavorite)
        {
            return;
        }
        _isChangingFavorite = true;
        int generation = _generation;
        bool favorite = !IsFavorite;
        ActionError = "";
        IsFavorite = favorite;
        try
        {
            await _catalog.SetFavoriteAsync(server, _contentId, favorite);
            _changes?.Report();
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Changing favorite {ItemId} failed: {Failure}", _contentId, ApiFailureText.Describe(ex));
            if (generation == _generation)
            {
                IsFavorite = !favorite;
                ActionError = DescribeActionFailure(favorite ? "add this to your favorites" : "remove this from your favorites", ex);
            }
        }
        finally
        {
            _isChangingFavorite = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleWatchlistAsync()
    {
        if (_serverBase is not { } server || !CanFavorite || _isChangingWatchlist)
        {
            return;
        }
        _isChangingWatchlist = true;
        int generation = _generation;
        bool add = !IsInWatchlist;
        ActionError = "";
        IsInWatchlist = add;
        try
        {
            await _catalog.SetWatchlistAsync(server, _contentId, add);
            _changes?.Report();
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Changing watchlist {ItemId} failed: {Failure}", _contentId, ApiFailureText.Describe(ex));
            if (generation == _generation)
            {
                IsInWatchlist = !add;
                ActionError = DescribeActionFailure(add ? "add this to your watchlist" : "remove this from your watchlist", ex);
            }
        }
        finally
        {
            _isChangingWatchlist = false;
        }
    }

    private void Open(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && id != ItemId && id != _contentId)
        {
            _navigation.NavigateTo(AppRoute.Item, id);
        }
    }

    // Marking watched or unwatched replaces the progress with a completed or empty entry: no resume point either way.
    private static WatchRollup Marked(WatchRollup? watch, bool watched, bool isLeaf)
    {
        var current = watch ?? new WatchRollup();
        int episodes = isLeaf ? 1 : current.WatchedCount + current.UnplayedCount;
        return current with
        {
            Played = watched,
            WatchedCount = watched ? Math.Max(episodes, 1) : 0,
            UnplayedCount = watched ? 0 : episodes,
            InProgressCount = 0,
            IsInProgress = false,
            PositionSeconds = null,
        };
    }

    private static Season Marked(Season season, bool watched) => season with
    {
        UserData = (season.UserData ?? new WatchRollup()) with
        {
            Played = watched,
            WatchedCount = watched ? season.EpisodeCount : 0,
            UnplayedCount = watched ? 0 : season.EpisodeCount,
            InProgressCount = 0,
            IsInProgress = false,
            PositionSeconds = null,
        },
    };

    private static Episode Marked(Episode episode, bool watched) => episode with
    {
        UserData = (episode.UserData ?? new WatchRollup()) with
        {
            Played = watched,
            WatchedCount = watched ? 1 : 0,
            UnplayedCount = watched ? 0 : 1,
            InProgressCount = 0,
            IsInProgress = false,
            PositionSeconds = null,
        },
    };

    private static string DescribeActionFailure(string action, Exception ex) => ex switch
    {
        SiloApiException { Status: 429 } api => $"Could not {action}. The server is receiving too many requests. " + ApiFailureText.TryAgainIn(api.RetryAfter),
        SiloApiException { Status: 503 } => $"Could not {action}. " + ApiFailureText.Busy,
        SiloApiException { Status: 404 } => $"Could not {action}: this title is no longer available.",
        SiloApiException api => $"Could not {action} (error {api.Status}). Try again.",
        _ => $"Could not {action}. Check the connection and try again.",
    };
}
