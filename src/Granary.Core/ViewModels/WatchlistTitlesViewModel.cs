using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// The watchlist's "Not in your library yet" tab, as Silo's web shows it (<c>components/watchlist/WatchlistTitlesTab.tsx</c>):
/// the titles saved from TMDB that the library does not have, soonest first, while the server keeps them
/// (<c>requests_enabled</c> and <c>watchlist_titles_supported</c>). Read on every visit of the watchlist, for the tab's count
/// and its attention dot too, and again while the watchlist shows: every <see cref="RequestsViewModel.DownloadPollInterval"/>
/// while a title downloads, and after the viewer changed the watchlist. A later read keeps what is shown while it runs, and a
/// failed one leaves it. A title a read no longer lists has arrived or was taken off: <see cref="TitlesLeft"/> tells the
/// library's grid. A card opens the title's page.
/// </summary>
public sealed partial class WatchlistTitlesViewModel : ObservableObject
{
    private readonly ISessionManager _session;
    private readonly RequestsApi _requests;
    private readonly RequestsFeature _feature;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<WatchlistTitlesViewModel> _logger;
    private readonly ViewerStateChanges? _changes;
    // Where each card opens: the title's page.
    private readonly Dictionary<string, TitleRouteParameter> _destinations = new(StringComparer.Ordinal);
    private CancellationTokenSource? _run;
    private ITimer? _poll;
    private bool _isActive;
    private bool _hasDownload;
    // This model is telling of a title that left: its own report needs no read.
    private bool _reporting;
    // Whose titles are shown, and which: a later read for them tells what left.
    private (string? ServerId, string? ProfileId)? _shownFor;
    private HashSet<string>? _shownIds;

    public WatchlistTitlesViewModel(ISessionManager session, RequestsApi requests, RequestsFeature feature, INavigationService navigation,
        TimeProvider time, IUiDispatcher dispatcher, ILogger<WatchlistTitlesViewModel> logger, ViewerStateChanges? changes = null)
    {
        _session = session;
        _requests = requests;
        _feature = feature;
        _navigation = navigation;
        _time = time;
        _dispatcher = dispatcher;
        _logger = logger;
        _changes = changes;
    }

    /// <summary>A title the last read listed is no longer on it: the library may have it now.</summary>
    public event EventHandler? TitlesLeft;

    /// <summary>The server keeps watchlist titles the library does not have: the watchlist shows its two tabs.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(HasError), nameof(IsReady))]
    public partial LibraryLoadState State { get; private set; }

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsEmpty => State == LibraryLoadState.Empty;

    public bool HasError => State == LibraryLoadState.Error;

    public bool IsReady => State == LibraryLoadState.Ready;

    [ObservableProperty]
    public partial IReadOnlyList<MediaCard> Cards { get; private set; } = [];

    /// <summary>How many titles, beside the tab's name; 0 until they are read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    public partial int Count { get; private set; }

    public bool HasCount => Count > 0;

    /// <summary>A title TMDB lost needs the viewer: the tab's amber dot.</summary>
    [ObservableProperty]
    public partial bool NeedsAttention { get; private set; }

    /// <summary>What the titles are and what happens when one arrives, over them.</summary>
    [ObservableProperty]
    public partial string Hint { get; private set; } = WatchlistTitleStatus.Hint(watchlistRequests: false);

    /// <summary>The read in flight, for tests.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The watchlist shows: reads the titles, and keeps them current while it shows.</summary>
    public Task ActivateAsync()
    {
        if (!_isActive)
        {
            _isActive = true;
            if (_changes is not null)
            {
                _changes.Changed += OnViewerStateChanged;
            }
        }
        return ReadAsync();
    }

    /// <summary>The watchlist left: no more reads until it shows again.</summary>
    public void Deactivate()
    {
        if (!_isActive)
        {
            return;
        }
        _isActive = false;
        if (_changes is not null)
        {
            _changes.Changed -= OnViewerStateChanged;
        }
        StopPolling();
        // A read that answers after the page left would reload a list nobody sees; the next visit reads afresh.
        _run?.Cancel();
        _run = null;
    }

    [RelayCommand]
    private Task RetryAsync() => ReadAsync();

    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null && _destinations.TryGetValue(card.Id, out var destination))
        {
            _navigation.NavigateTo(AppRoute.Title, destination);
        }
    }

    [RelayCommand]
    private void BrowseDiscover() => _navigation.NavigateTo(AppRoute.Requests);

    private Task ReadAsync()
    {
        _run?.Cancel();
        var run = new CancellationTokenSource();
        _run = run;
        return Loading = ReadAsync(run);
    }

    private async Task ReadAsync(CancellationTokenSource run)
    {
        var cancellationToken = run.Token;
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            Clear();
            return;
        }
        var key = (_session.ActiveServer?.Id, _session.ProfileId);
        if (_shownFor != key)
        {
            // Another profile: nothing of the last one's is shown, even while its own are read.
            Clear();
            _shownFor = key;
        }
        bool quietly = State is LibraryLoadState.Ready or LibraryLoadState.Empty;
        try
        {
            // Read once per profile; a read in flight, the sidebar's, is waited for.
            await _feature.RefreshAsync().WaitAsync(cancellationToken);
            IsAvailable = _feature.Status is { RequestsEnabled: true, WatchlistTitlesSupported: true };
            if (!IsAvailable)
            {
                Clear();
                _shownFor = key;
                return;
            }
            Hint = WatchlistTitleStatus.Hint(_feature.Status?.WatchlistRequests == true);
            if (!quietly)
            {
                State = LibraryLoadState.Loading;
            }
            var titles = await _requests.ListWatchlistTitlesAsync(server.BaseUri, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Pages read after a profile switch are the next profile's: the read is no one's.
            if ((_session.ActiveServer?.Id, _session.ProfileId) == key)
            {
                Show(titles);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Reading the watchlist's titles outside the library failed: {Failure}", ApiFailureText.Describe(ex));
            // A failed later read leaves what is shown; a failed first one says so, with Retry.
            if (!quietly)
            {
                State = LibraryLoadState.Error;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer read replaced this one.
        }
    }

    private void Show(IReadOnlyList<WatchlistTitle> titles)
    {
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var ids = titles.Select(title => RequestCards.IdOf(title.MediaType, title.TmdbId)).ToHashSet(StringComparer.Ordinal);
        bool someLeft = _shownIds is { } shown && shown.Any(id => !ids.Contains(id));
        _shownIds = ids;

        _destinations.Clear();
        var cards = new List<MediaCard>(titles.Count);
        foreach (var title in WatchlistTitleStatus.SoonestFirst(titles, now, zone))
        {
            if (title.TmdbId <= 0 || title.MediaType is not ("movie" or "series"))
            {
                continue;
            }
            var card = RequestCards.Of(title, WatchlistTitleStatus.Of(title, now, zone));
            if (_destinations.TryAdd(card.Id, new TitleRouteParameter(title.MediaType, title.TmdbId, title.Title)))
            {
                cards.Add(card);
            }
        }
        Cards = cards;
        Count = cards.Count;
        NeedsAttention = titles.Any(WatchlistTitleStatus.NeedsAttention);
        State = cards.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        _hasDownload = titles.Any(title => title.Request.Download is not null);
        FollowDownloads();
        if (someLeft)
        {
            // Home's rows and the titles' pages may list it on the watchlist now. Told first, so the library's grid, read again
            // below, counts as current.
            _reporting = true;
            try
            {
                _changes?.Report();
            }
            finally
            {
                _reporting = false;
            }
            TitlesLeft?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Clear()
    {
        _shownFor = null;
        _shownIds = null;
        _destinations.Clear();
        IsAvailable = false;
        Cards = [];
        Count = 0;
        NeedsAttention = false;
        State = LibraryLoadState.Idle;
        _hasDownload = false;
        StopPolling();
    }

    // While a title downloads, its progress is read again on the interval the other request pages use.
    private void FollowDownloads()
    {
        if (_isActive && _hasDownload)
        {
            _poll ??= _time.CreateTimer(_ => _dispatcher.Post(OnPollTick), null, RequestsViewModel.DownloadPollInterval,
                RequestsViewModel.DownloadPollInterval);
        }
        else
        {
            StopPolling();
        }
    }

    private void StopPolling()
    {
        _poll?.Dispose();
        _poll = null;
    }

    private void OnPoll()
    {
        if (_isActive)
        {
            _ = ReadAsync();
        }
    }

    // A slow read (many pages) is let finish rather than replaced by the next tick.
    private void OnPollTick()
    {
        if (Loading.IsCompleted)
        {
            OnPoll();
        }
    }

    // A card took a title off the watchlist, or put one on; whichever thread reported it.
    private void OnViewerStateChanged(object? sender, EventArgs e)
    {
        if (!_reporting)
        {
            _dispatcher.Post(OnPoll);
        }
    }
}
