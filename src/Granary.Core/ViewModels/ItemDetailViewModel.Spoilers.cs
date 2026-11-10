using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels;

// Spoiler protection on the page, as the web's SeasonEpisodeGrid, EpisodeCarousel and EpisodeContent apply the profile's
// switches (EpisodeSpoilerSettings): an unwatched episode's still blurred and its description left out, the page's own
// episode's description behind "Show it". While the page shows it follows the switches, and the viewer's state: a stop of
// the player above it, as Silo's apps read their page again once the player is gone (Android's refreshOnReturn, Apple's
// reload after the player's cleanup), or a start or a watched mark elsewhere moves the Play pill, the progress and the blur.
public sealed partial class ItemDetailViewModel
{
    /// <summary>The web's words in place of a hidden description (<c>DetailOverview.tsx</c>).</summary>
    public const string OverviewHiddenText = "Description hidden until you watch. Show it";

    /// <summary>How long the page waits after a change elsewhere before it reads the viewer's state again: a burst is read once.</summary>
    public static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(400);

    // The episode whose description the viewer asked to see: a reveal lasts for that episode, not the next one.
    private string? _revealedOverviewId;
    private bool _isActive;
    private bool _isReportingOwnChange;
    // Counts the page's own watched changes: a read of the state that began before one does not put the old state back.
    private int _watchEdits;
    // A change elsewhere not read yet: it came while the page marked watched.
    private bool _watchStateStale;
    private ITimer? _refreshTimer;
    private CancellationTokenSource? _refresh;

    /// <summary>The episode's own description is hidden until it is started; <see cref="RevealOverviewCommand"/> shows it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOverview))]
    public partial bool IsOverviewHidden { get; private set; }

    /// <summary>The read of the viewer's state after a change elsewhere, for tests.</summary>
    internal Task Refreshing { get; private set; } = Task.CompletedTask;

    private EpisodeSpoilers Spoilers => _spoilers?.Shown ?? EpisodeSpoilers.Off;

    /// <summary>The page shows: follow the spoiler switches and the viewer's state, and show switches changed while away.</summary>
    public void Activate()
    {
        if (_isActive)
        {
            return;
        }
        _isActive = true;
        _spoilers?.PropertyChanged += OnSpoilersChanged;
        _changes?.Changed += OnViewerStateChanged;
        ShowSpoilers();
        FollowTitlePage();
    }

    /// <summary>The page left: stop following, and drop a read of the state, and the placing of missing episodes, on its way.</summary>
    public void Deactivate()
    {
        StopPlacing();
        if (!_isActive)
        {
            return;
        }
        _isActive = false;
        LeaveTitlePage();
        _spoilers?.PropertyChanged -= OnSpoilersChanged;
        _changes?.Changed -= OnViewerStateChanged;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _refresh?.Cancel();
        _refresh?.Dispose();
        _refresh = null;
        _watchStateStale = false;
    }

    [RelayCommand]
    private void RevealOverview()
    {
        _revealedOverviewId = _contentId;
        ShowOverview();
    }

    private void ShowSpoilers()
    {
        if (_listsEpisodes)
        {
            ShowEpisodeCards();
        }
        ShowSiblingCards();
        ShowOverview();
    }

    // An episode's own description, while the episode is neither played nor started, unless the viewer asked to see it; the
    // Info dialog leaves it out then too.
    private void ShowOverview()
    {
        if (_item is not { Type: CatalogItemTypes.Episode } item)
        {
            IsOverviewHidden = false;
            return;
        }
        bool hidden = HasOverview && _revealedOverviewId != _contentId
            && Spoilers.HidesOverview(!IsWatched && EpisodeSpoilers.IsUnwatched(_watch));
        if (hidden != IsOverviewHidden)
        {
            IsOverviewHidden = hidden;
            InfoSections = DetailFacts.InfoSections(item, withOverview: !hidden);
        }
    }

    // Only a season of more than one episode has a row, as on the web.
    private void ShowSiblings(IReadOnlyList<Episode> episodes)
    {
        _siblings = episodes.Where(e => e.ContentId.Length > 0).ToList() is { Count: > 1 } siblings ? siblings : [];
        ShowSiblingCards();
    }

    private void ShowSiblingCards()
    {
        if (_serverBase is not { } server)
        {
            return;
        }
        var spoilers = Spoilers;
        // Matched by content id, as the apps match the episode they show.
        IReadOnlyList<MediaCard> cards = [.. _siblings.Select(e => MediaCardFactory.Episode(e, server, spoilers) with
        {
            CurrentMark = e.ContentId == _contentId ? CurrentMark.NowViewing : CurrentMark.None,
        })];
        // A new list centres the row on the episode again: only for cards that changed.
        if (!cards.SequenceEqual(MoreEpisodes))
        {
            MoreEpisodes = cards;
        }
    }

    // Whatever thread the change came from, the page changes on its own.
    private void OnUiThread(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.Post(action);
        }
    }

    private void OnSpoilersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EpisodeSpoilerSettings.Shown))
        {
            OnUiThread(() =>
            {
                if (_isActive)
                {
                    ShowSpoilers();
                }
            });
        }
    }

    // The Play pill and the progress follow the state whatever the switches hide. A change while the page marks watched is
    // read once that is done.
    private void OnViewerStateChanged(object? sender, EventArgs e) => OnUiThread(() =>
    {
        if (!_isActive || _isReportingOwnChange)
        {
            return;
        }
        if (_isChangingWatched)
        {
            _watchStateStale = true;
            return;
        }
        ScheduleRefresh();
    });

    // Each change waits the delay again, so marking several cards in a row reads the lists once.
    private void ScheduleRefresh()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = _time.CreateTimer(static page => ((ItemDetailViewModel)page!).OnRefreshDue(), this, RefreshDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnRefreshDue() => OnUiThread(() =>
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        if (_isActive)
        {
            Refreshing = RefreshWatchStateAsync();
        }
    });

    // After the page's own watched change: what changed elsewhere meanwhile.
    private void RefreshIfStale()
    {
        if (_watchStateStale && _isActive)
        {
            _watchStateStale = false;
            Refreshing = RefreshWatchStateAsync();
        }
    }

    // The page's own change is shown already; only the others' are read again.
    private void ReportOwnChange()
    {
        _isReportingOwnChange = true;
        try
        {
            _changes?.Report();
        }
        finally
        {
            _isReportingOwnChange = false;
        }
    }

    // A watched mark, a start or a stop: the item's own state (where Play resumes, a series' or season's next episode), its
    // listed episodes, a series' season cards and an episode's siblings, read again with the image sizes the page loaded
    // with, so their stills stay the ones shown. A read that fails keeps what the page shows.
    private async Task RefreshWatchStateAsync()
    {
        if (State != ItemDetailState.Ready || _item is not { } item || _serverBase is not { } server)
        {
            return;
        }
        _refresh?.Cancel();
        _refresh?.Dispose();
        var run = new CancellationTokenSource();
        _refresh = run;
        var cancellationToken = run.Token;
        int generation = _generation;
        int edits = _watchEdits;
        var images = _imageParameters;
        try
        {
            var itemRead = TryLoadAsync("watch state", () => _catalog.GetItemAsync(server, _contentId, images: images, cancellationToken: cancellationToken),
                cancellationToken);
            var listRead = ListToRefresh(server, item, images, cancellationToken);
            var seasonsRead = SeasonsToRefresh(server, item, images, cancellationToken);
            // All are awaited, so a cancelled one does not leave the others unobserved.
            await Task.WhenAll(itemRead, listRead, seasonsRead);
            var fresh = await itemRead;
            var list = await listRead;
            var seasons = await seasonsRead;
            if (cancellationToken.IsCancellationRequested || generation != _generation)
            {
                return;
            }
            if (edits != _watchEdits || _isChangingWatched)
            {
                // Older than the page's own change: read again once that is done.
                _watchStateStale = true;
                if (!_isChangingWatched)
                {
                    RefreshIfStale();
                }
                return;
            }
            if (fresh is not null)
            {
                // Only what the viewer's state moves; the rest stays as the page read it.
                _item = item with { UserData = fresh.UserData, PlayContentId = fresh.PlayContentId, PlaySeasonNumber = fresh.PlaySeasonNumber };
                _watch = fresh.UserData;
                IsWatched = fresh.UserData?.Played ?? fresh.UserState?.Played ?? false;
            }
            if (list is not null && _listsEpisodes)
            {
                _episodes = list.Items;
                ShowEpisodeCards();
            }
            else if (list is not null)
            {
                ShowSiblings(list.Items);
            }
            if (seasons is not null)
            {
                _seasons = OrderSeasons(seasons.Items);
                ShowSeasonCards();
            }
            ShowPlayAction();
            ShowOverview();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The page left, or a newer change is read instead.
        }
    }

    private Task<EpisodeCollection?> ListToRefresh(Uri server, CatalogItemDetail item, Api.Images.ImageParameters images, CancellationToken cancellationToken)
    {
        if (_listsEpisodes && _episodesSeasonId is { } seasonId)
        {
            return TryLoadAsync("episodes", () => _catalog.ListSeasonEpisodesAsync(server, seasonId, images: images, cancellationToken: cancellationToken),
                cancellationToken);
        }
        if (_siblings.Count > 0 && !string.IsNullOrWhiteSpace(item.SeriesId) && item.SeasonNumber is { } number)
        {
            string seriesId = item.SeriesId;
            return TryLoadAsync("episodes", () => _catalog.ListEpisodesAsync(server, seriesId, number, images: images, cancellationToken: cancellationToken),
                cancellationToken);
        }
        return Task.FromResult<EpisodeCollection?>(null);
    }

    // A series' season cards count its watched episodes; a single-season series lists its episodes instead.
    private Task<SeasonCollection?> SeasonsToRefresh(Uri server, CatalogItemDetail item, Api.Images.ImageParameters images, CancellationToken cancellationToken)
    {
        if (item.Type != CatalogItemTypes.Series || _listsEpisodes || _seasons.Count == 0)
        {
            return Task.FromResult<SeasonCollection?>(null);
        }
        string seriesId = _contentId;
        return TryLoadAsync("seasons", () => _catalog.ListSeasonsAsync(server, seriesId, images: images, cancellationToken: cancellationToken), cancellationToken);
    }
}
