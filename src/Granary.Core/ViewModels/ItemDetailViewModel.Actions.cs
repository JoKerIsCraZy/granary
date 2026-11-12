using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Texts;
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
        _watchEdits++;
        DropRefreshOnItsWay();
        int generation = _generation;
        bool watched = !IsWatched;
        var before = (Watch: _watch, IsWatched, Seasons: _seasons, Episodes: _episodes, Siblings: _siblings);
        ActionError = "";
        _watch = Marked(_watch, watched, item.Type is CatalogItemTypes.Movie or CatalogItemTypes.Episode);
        IsWatched = watched;
        _seasons = [.. _seasons.Select(s => Marked(s, watched))];
        _episodes = [.. _episodes.Select(e => Marked(e, watched))];
        // An episode's own card in More Episodes, too.
        _siblings = [.. _siblings.Select(e => e.ContentId == _contentId ? Marked(e, watched) : e)];
        var marked = (Seasons: _seasons, Episodes: _episodes, Siblings: _siblings);
        ShowWatchState();
        try
        {
            await _catalog.SetWatchedAsync(server, _contentId, watched);
            ReportOwnChange();
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
                _siblings = ReferenceEquals(_siblings, marked.Siblings) ? before.Siblings : _siblings;
                ShowWatchState();
                ActionError = DescribeWriteFailure(watched ? DetailWrite.MarkWatched : DetailWrite.MarkUnwatched, ex);
            }
        }
        finally
        {
            _isChangingWatched = false;
            RefreshIfStale();
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
            ReportOwnChange();
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Changing favorite {ItemId} failed: {Failure}", _contentId, ApiFailureText.Describe(ex));
            if (generation == _generation)
            {
                IsFavorite = !favorite;
                ActionError = DescribeWriteFailure(favorite ? DetailWrite.AddFavorite : DetailWrite.RemoveFavorite, ex);
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
            ReportOwnChange();
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Changing watchlist {ItemId} failed: {Failure}", _contentId, ApiFailureText.Describe(ex));
            if (generation == _generation)
            {
                IsInWatchlist = !add;
                ActionError = DescribeWriteFailure(add ? DetailWrite.AddWatchlist : DetailWrite.RemoveWatchlist, ex);
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

    /// <summary>The writes of the page whose failure it words.</summary>
    private enum DetailWrite
    {
        MarkWatched,
        MarkUnwatched,
        AddFavorite,
        RemoveFavorite,
        AddWatchlist,
        RemoveWatchlist,
        Rating,
    }

    // Why a write of the page did not stick, after what failed ("Could not add this to your favorites", the web's "Failed to
    // update rating"): one whole sentence for each write and reason, the wait the server asks for inside it.
    private static string DescribeWriteFailure(DetailWrite write, Exception ex)
    {
        var (reason, count, status) = ex switch
        {
            SiloApiException { Status: 429, RetryAfter: { } wait } => ("wait", ApiFailureText.WholeMinutes(wait), 0),
            SiloApiException { Status: 429 } => ("later", 0, 0),
            SiloApiException { Status: 503 } => ("busy", 0, 0),
            SiloApiException { Status: 404 } => ("gone", 0, 0),
            SiloApiException api => ("error", 0, api.Status),
            _ => ("connection", 0, 0),
        };
        return write switch
        {
            DetailWrite.MarkWatched => AppText.Get("granary.detail.markWatchedFailed", ("reason", reason), ("count", count), ("status", status)),
            DetailWrite.MarkUnwatched => AppText.Get("granary.detail.markUnwatchedFailed", ("reason", reason), ("count", count), ("status", status)),
            DetailWrite.AddFavorite => AppText.Get("granary.detail.addFavoriteFailed", ("reason", reason), ("count", count), ("status", status)),
            DetailWrite.RemoveFavorite => AppText.Get("granary.detail.removeFavoriteFailed", ("reason", reason), ("count", count), ("status", status)),
            DetailWrite.AddWatchlist => AppText.Get("granary.detail.addWatchlistFailed", ("reason", reason), ("count", count), ("status", status)),
            DetailWrite.RemoveWatchlist => AppText.Get("granary.detail.removeWatchlistFailed", ("reason", reason), ("count", count), ("status", status)),
            _ => AppText.Get("granary.detail.ratingFailed", ("reason", reason), ("count", count), ("status", status)),
        };
    }
}
