using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>Where the box set page stands.</summary>
public enum BoxSetPageState
{
    Loading,
    Ready,

    /// <summary>404: the library has box sets off, or the viewer sees too few of its films there.</summary>
    Unavailable,

    Failed,
}

/// <summary>
/// One movie box set (docs/api-contract.md, 7.18), as the web's <c>pages/BoxSetPage.tsx</c> shows it: "BOX SET" over its
/// name, its facts and overview, Play for the film to watch next, then its films in release order. While the page shows, a
/// change of the viewer's state (a film marked watched on its card, one played) reads it again, so the facts and Play
/// follow, as Android's <c>BoxSetViewModel</c> does; that read failing keeps the page as it is, unless the box set is gone (404).
/// </summary>
public sealed partial class BoxSetViewModel : ObservableObject
{
    public const string Overline = "BOX SET";
    public const string UnavailableTitle = "This box set isn't available";
    public const string UnavailableText = "Box sets may be turned off for this library, or too few of its movies are here.";
    public const string FailedTitle = "Couldn't load this box set";
    public const string FailedText = "Something went wrong while loading it. Try again in a moment.";

    /// <summary>How long the page waits after a change of the viewer's state before it reads again: a burst is read once.</summary>
    public static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(400);

    private readonly ISessionManager _session;
    private readonly BoxSetsApi _boxSets;
    private readonly ImageParameterCache _images;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<BoxSetViewModel> _logger;
    private readonly ViewerStateChanges? _changes;

    // A newer load or read makes the answers of older ones stale.
    private int _generation;
    private BoxSetRouteParameter? _route;
    private ImageParameters _imageParameters = ImageParameters.None;
    private CatalogItem? _next;
    private bool _isActive;
    private ITimer? _refreshTimer;
    // The read after a change of the viewer's state; a newer one, or leaving the page, cancels it.
    private CancellationTokenSource? _refreshRun;

    public BoxSetViewModel(ISessionManager session, BoxSetsApi boxSets, ImageParameterCache images, INavigationService navigation,
        IUiDispatcher dispatcher, TimeProvider time, ILogger<BoxSetViewModel> logger, ViewerStateChanges? changes = null)
    {
        _session = session;
        _boxSets = boxSets;
        _images = images;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _changes = changes;
    }

    /// <summary>The display scale artwork is asked for (1.5 at 150 %); set it before loading.</summary>
    public double DisplayScale { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(IsUnavailable), nameof(HasError))]
    public partial BoxSetPageState State { get; private set; }

    public bool IsLoading => State == BoxSetPageState.Loading;

    public bool IsReady => State == BoxSetPageState.Ready;

    public bool IsUnavailable => State == BoxSetPageState.Unavailable;

    public bool HasError => State == BoxSetPageState.Failed;

    /// <summary>The box set's name; the route's while it loads.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial string Overview { get; private set; } = "";

    [ObservableProperty]
    public partial Uri? PosterUrl { get; private set; }

    /// <summary>The poster's placeholder; "" for none, as x:Bind skips a function whose argument is null.</summary>
    [ObservableProperty]
    public partial string PosterThumbhash { get; private set; } = "";

    [ObservableProperty]
    public partial Uri? BackdropUrl { get; private set; }

    /// <summary>The backdrop's placeholder; "" for none.</summary>
    [ObservableProperty]
    public partial string BackdropThumbhash { get; private set; } = "";

    /// <summary>The web's metadata badges in its capitals: "3 FILMS", "2019 – 2028", "1 OF 3 WATCHED".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DetailChip> Facts { get; private set; } = [];

    /// <summary>"Play Groundwork" or "Continue · Masterplan"; empty while there is no film to play.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    public partial string PlayLabel { get; private set; } = "";

    public bool CanPlay => PlayLabel.Length > 0;

    /// <summary>The films in release order, as library cards.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MediaCard> Films { get; private set; } = [];

    /// <summary>The read after a change of the viewer's state, for tests.</summary>
    internal Task Refreshing { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the box set; executing it again cancels the running load.</summary>
    [RelayCommand]
    private async Task LoadAsync(BoxSetRouteParameter? route, CancellationToken cancellationToken)
    {
        int generation = ++_generation;
        _route = route;
        Show(null);
        Title = route?.Title ?? "";
        State = BoxSetPageState.Loading;
        if (route is null || _session.ActiveServer is not { } server)
        {
            State = BoxSetPageState.Failed;
            return;
        }
        try
        {
            _imageParameters = await BrowseArtwork.ParametersAsync(_images, server, DisplayScale, _logger, cancellationToken);
            var boxSet = await _boxSets.GetAsync(server.BaseUri, route.BoxSetId, route.LibraryId, _imageParameters, cancellationToken);
            if (generation == _generation)
            {
                Show(boxSet);
                State = BoxSetPageState.Ready;
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex) || ex is ArgumentException)
        {
            if (generation != _generation || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            if (ex is SiloApiException { Status: 404 })
            {
                State = BoxSetPageState.Unavailable;
                return;
            }
            _logger.LogWarning("Loading box set {BoxSetId} failed: {Failure}", route.BoxSetId, ApiFailureText.Describe(ex));
            State = BoxSetPageState.Failed;
        }
    }

    [RelayCommand]
    private Task RetryAsync() => LoadCommand.ExecuteAsync(_route);

    /// <summary>Plays the film to watch next, from where the viewer left it.</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (_next is { ContentId.Length: > 0 } next)
        {
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.Resume(next.ContentId));
        }
    }

    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is { Id.Length: > 0 })
        {
            _navigation.NavigateTo(AppRoute.Item, card.Id);
        }
    }

    /// <summary>The page shows: follow the viewer's state.</summary>
    public void Activate()
    {
        if (!_isActive && _changes is not null)
        {
            _isActive = true;
            _changes.Changed += OnViewerStateChanged;
        }
    }

    /// <summary>The page left: stop following, and drop a read on its way.</summary>
    public void Deactivate()
    {
        if (_isActive && _changes is not null)
        {
            _isActive = false;
            _changes.Changed -= OnViewerStateChanged;
        }
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        StopRefresh();
        LoadCommand.Cancel();
        _generation++;
    }

    // Changed comes on whichever thread made the change.
    private void OnViewerStateChanged(object? sender, EventArgs e) => _dispatcher.Post(ScheduleRefresh);

    private void ScheduleRefresh()
    {
        if (!_isActive || State != BoxSetPageState.Ready)
        {
            return;
        }
        _refreshTimer?.Dispose();
        _refreshTimer = _time.CreateTimer(static state => ((BoxSetViewModel)state!).RefreshSoon(), this, RefreshDelay, Timeout.InfiniteTimeSpan);
    }

    private void RefreshSoon() => _dispatcher.Post(() =>
    {
        if (_isActive)
        {
            Refreshing = RefreshAsync();
        }
    });

    private async Task RefreshAsync()
    {
        if (_route is not { } route || _session.ActiveServer is not { } server || State != BoxSetPageState.Ready)
        {
            return;
        }
        int generation = ++_generation;
        StopRefresh();
        var run = new CancellationTokenSource();
        _refreshRun = run;
        try
        {
            var boxSet = await _boxSets.GetAsync(server.BaseUri, route.BoxSetId, route.LibraryId, _imageParameters, run.Token);
            if (generation == _generation)
            {
                Show(boxSet);
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (run.IsCancellationRequested || generation != _generation)
            {
                return;
            }
            // Gone meanwhile (box sets turned off, too few films left to see), as Android's refresh says so.
            if (ex is SiloApiException { Status: 404 })
            {
                Show(null);
                State = BoxSetPageState.Unavailable;
                return;
            }
            _logger.LogWarning("Reading box set {BoxSetId} again failed: {Failure}", route.BoxSetId, ApiFailureText.Describe(ex));
        }
    }

    private void StopRefresh()
    {
        _refreshRun?.Cancel();
        _refreshRun?.Dispose();
        _refreshRun = null;
    }

    private void Show(CatalogBoxSetDetail? boxSet)
    {
        var server = _session.ActiveServer?.BaseUri;
        Title = boxSet?.Title ?? Title;
        Overview = boxSet?.Overview?.Trim() ?? "";
        PosterUrl = server is null ? null : ArtworkUrl.Resolve(server, boxSet?.PosterUrl);
        PosterThumbhash = boxSet?.PosterThumbhash ?? "";
        BackdropUrl = server is null ? null : ArtworkUrl.Resolve(server, boxSet?.BackdropUrl);
        BackdropThumbhash = boxSet?.BackdropThumbhash ?? "";
        Facts = boxSet is null ? [] : [.. BoxSetText.Facts(boxSet).Select(fact => new DetailChip(fact.ToUpperInvariant()))];
        // A film without an id could not be opened or played: it gets no card, and Play passes it over.
        IReadOnlyList<CatalogItem> films = boxSet is null ? [] : [.. boxSet.Items.Where(film => film.ContentId.Length > 0)];
        Films = server is null ? [] : [.. films.Select(film => MediaCardFactory.Poster(film, server))];
        _next = BoxSetText.NextFilm(films);
        PlayLabel = boxSet is not null && _next is { } next ? BoxSetText.PlayLabel(boxSet, next) : "";
    }
}
