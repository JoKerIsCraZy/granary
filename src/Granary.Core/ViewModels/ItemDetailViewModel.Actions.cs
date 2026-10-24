using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// Navigation and the watched, favourite and watchlist actions of the detail page.
public sealed partial class ItemDetailViewModel
{
    [RelayCommand]
    private void OpenCard(MediaCard? card) => Open(card?.Id);

    [RelayCommand]
    private void OpenEpisode(DetailEpisode? episode) => Open(episode?.Id);

    [RelayCommand]
    private void OpenCrumb(DetailCrumb? crumb) => Open(crumb?.TargetId);

    [RelayCommand]
    private void GoBack() => _navigation.GoBack();

    /// <summary>Marks the item watched or unwatched; a series or season covers its episodes, and both clear the resume point.</summary>
    [RelayCommand]
    private async Task ToggleWatchedAsync()
    {
        if (_item is not { } item || _serverBase is not { } server || !ShowsWatchedAction)
        {
            return;
        }
        int generation = _generation;
        bool watched = !IsWatched;
        var before = (_watch, IsWatched, _seasons, _episodes, MoreEpisodes);
        ActionError = "";
        _watch = Marked(_watch, watched, item.Type is CatalogItemTypes.Movie or CatalogItemTypes.Episode);
        IsWatched = watched;
        _seasons = [.. _seasons.Select(s => Marked(s, watched))];
        _episodes = [.. _episodes.Select(e => Marked(e, watched))];
        MoreEpisodes = [.. MoreEpisodes.Select(c => c.Id == item.ContentId ? c with { IsWatched = watched, Progress = null } : c)];
        ShowWatchState();
        try
        {
            await _catalog.SetWatchedAsync(server, item.ContentId, watched);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Marking item {ItemId} {Change} failed: {Failure}", item.ContentId, watched ? "watched" : "unwatched", ApiFailureText.Describe(ex));
            if (generation != _generation)
            {
                return;
            }
            (_watch, IsWatched, _seasons, _episodes, MoreEpisodes) = before;
            ShowWatchState();
            ActionError = DescribeActionFailure(watched ? "mark this as watched" : "mark this as unwatched", ex);
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (_item is not { } item || _serverBase is not { } server || !CanFavorite)
        {
            return;
        }
        int generation = _generation;
        bool favorite = !IsFavorite;
        ActionError = "";
        IsFavorite = favorite;
        try
        {
            await _catalog.SetFavoriteAsync(server, item.ContentId, favorite);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Changing favorite {ItemId} failed: {Failure}", item.ContentId, ApiFailureText.Describe(ex));
            if (generation == _generation)
            {
                IsFavorite = !favorite;
                ActionError = DescribeActionFailure(favorite ? "add this to your favorites" : "remove this from your favorites", ex);
            }
        }
    }

    [RelayCommand]
    private async Task ToggleWatchlistAsync()
    {
        if (_item is not { } item || _serverBase is not { } server || !CanFavorite)
        {
            return;
        }
        int generation = _generation;
        bool add = !IsInWatchlist;
        ActionError = "";
        IsInWatchlist = add;
        try
        {
            await _catalog.SetWatchlistAsync(server, item.ContentId, add);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Changing watchlist {ItemId} failed: {Failure}", item.ContentId, ApiFailureText.Describe(ex));
            if (generation == _generation)
            {
                IsInWatchlist = !add;
                ActionError = DescribeActionFailure(add ? "add this to your watchlist" : "remove this from your watchlist", ex);
            }
        }
    }

    private void Open(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && id != ItemId)
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
