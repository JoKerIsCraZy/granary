using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Servers;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>The views of a library page, as on the web: its rows, all its titles, its collections.</summary>
public enum LibraryTab
{
    Recommended,
    Library,
    Collections,
}

/// <summary>
/// One library (<see cref="AppRoute.Library"/>, the parameter is its id), laid out like Silo's web client:
/// "LIBRARY / NAME", Shuffle, and the Recommended, Library and Collections tabs. Each tab loads the first time
/// it shows. The page keeps its state while the same library, server and profile are shown, so Back returns
/// to the same tab; another server or profile loads the library again. A library opens on the tab it was last left
/// on (<see cref="LibraryTabs"/>), as the web's "Remember library view", else on Recommended.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    public const string ShuffleNothingPlayable = "Nothing in this library can be played.";
    public const string ShuffleFailed = "Couldn't start a shuffle. Try again.";

    /// <summary>
    /// How long a visit may show what an earlier one loaded. Artwork URLs are presigned for a few hours
    /// (docs/api-contract.md, 7.10), so a page kept longer reloads instead of showing expired posters.
    /// </summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(1);

    // The library types whose movies and episodes a shuffle plays (web: SHUFFLE_LIBRARY_TYPES).
    private static readonly HashSet<string> ShuffleLibraryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "movie", "movies", "series", "tv", "show", "shows", "tvshows", "mixed",
    };

    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly VisibleLibraries _libraries;
    private readonly LibraryPageApi _pages;
    private readonly ImageParameterCache _images;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly ViewerStateChanges? _changes;
    private readonly LibraryTabs? _tabs;
    private readonly Dictionary<(string ServerId, string? ProfileId), bool> _serverCanShuffle = [];
    private (string? ServerId, string? ProfileId, string? LibraryId)? _loaded;
    private DateTimeOffset _loadedAt;
    private long _rowsVersion;
    private CancellationTokenSource _run = new();
    private ServerEntry? _server;
    private Library? _library;
    private ImageSizes _sizes = ImageSizes.Default;
    private bool _isActive;

    public LibraryViewModel(
        ISessionManager session,
        CatalogApi catalog,
        VisibleLibraries libraries,
        LibraryPageApi pages,
        ImageParameterCache images,
        INavigationService navigation,
        IUiDispatcher dispatcher,
        TimeProvider time,
        ILogger<LibraryViewModel> logger,
        ViewerStateChanges? changes = null,
        LibraryTabs? tabs = null)
    {
        _session = session;
        _catalog = catalog;
        _libraries = libraries;
        _pages = pages;
        _images = images;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _changes = changes;
        _tabs = tabs;
        Recommended = new LibraryRowsViewModel(catalog, navigation, logger);
        Browse = new LibraryBrowseViewModel(catalog, images, logger);
        Collections = new LibraryCollectionsViewModel(pages, catalog, images, logger);
    }

    public LibraryRowsViewModel Recommended { get; }

    public LibraryBrowseViewModel Browse { get; }

    public LibraryCollectionsViewModel Collections { get; }

    /// <summary>Loading, Ready, Unavailable (unknown or hidden id) or Error; Idle without a usable session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(IsUnavailable), nameof(HasError))]
    public partial LibraryLoadState State { get; private set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; private set; } = "";

    /// <summary>The id the page was opened with.</summary>
    [ObservableProperty]
    public partial string? LibraryId { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Breadcrumb))]
    public partial string LibraryName { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecommendedTab), nameof(IsLibraryTab), nameof(IsCollectionsTab))]
    public partial LibraryTab SelectedTab { get; private set; }

    /// <summary>Shuffle shows only for movie, series and mixed libraries on a server that can shuffle them.</summary>
    [ObservableProperty]
    public partial bool CanShuffle { get; private set; }

    /// <summary>Why a shuffle did not start; shown until dismissed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShuffleMessage))]
    public partial string ShuffleMessage { get; set; } = "";

    /// <summary>The display scale artwork is requested for (1.5 at 150 %); set before <see cref="ActivateAsync"/>.</summary>
    public double DisplayScale { get; set; } = 1;

    /// <summary>"LIBRARY / FILME", as the web's eyebrow.</summary>
    public string Breadcrumb => LibraryName.Length > 0 ? $"LIBRARY / {LibraryName.ToUpperInvariant()}" : "LIBRARY";

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsReady => State == LibraryLoadState.Ready;

    public bool IsUnavailable => State == LibraryLoadState.Unavailable;

    public bool HasError => State == LibraryLoadState.Error;

    public bool IsRecommendedTab => SelectedTab == LibraryTab.Recommended;

    public bool IsLibraryTab => SelectedTab == LibraryTab.Library;

    public bool IsCollectionsTab => SelectedTab == LibraryTab.Collections;

    public bool HasShuffleMessage
    {
        get => ShuffleMessage.Length > 0;
        set
        {
            if (!value)
            {
                ShuffleMessage = "";
            }
        }
    }

    /// <summary>Raised when another library (or the same one for another session) starts loading.</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>
    /// Shows the library with this id: on every visit of the page. A visit of what is already shown changes
    /// nothing, so Back keeps the tab, the grid and its scroll position; after <see cref="RefreshAfter"/> the
    /// same tab loads again.
    /// </summary>
    public async Task ActivateAsync(string? libraryId)
    {
        if (!_isActive)
        {
            _isActive = true;
            _session.Changed += OnSessionChanged;
        }
        bool isFresh = _time.GetUtcNow() - _loadedAt < RefreshAfter;
        if (_loaded == Key(libraryId) && (State == LibraryLoadState.Loading || (State == LibraryLoadState.Ready && isFresh)))
        {
            await RefreshRowsIfStaleAsync();
            return;
        }
        await LoadAsync(libraryId);
    }

    /// <summary>The page left: stop following the session until the next visit.</summary>
    public void Deactivate()
    {
        if (_isActive)
        {
            _isActive = false;
            _session.Changed -= OnSessionChanged;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SelectTabAsync(LibraryTab tab)
    {
        SelectedTab = tab;
        var remember = _tabs is not null && _loaded is { ServerId: { } server, ProfileId: var profile, LibraryId: { Length: > 0 } library }
            ? _tabs.RememberAsync(server, profile, library, tab)
            : Task.CompletedTask;
        return Task.WhenAll(remember, State == LibraryLoadState.Ready ? StartTabAsync(tab, _run.Token) : Task.CompletedTask);
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync(LibraryId);

    /// <summary>Opens a title of a grid; takes its <see cref="MediaCard"/>.</summary>
    [RelayCommand]
    private void OpenItem(MediaCard? card)
    {
        // A placeholder is a title not loaded yet: there is nothing to open.
        if (card is { IsPlaceholder: false })
        {
            _navigation.NavigateTo(AppRoute.Item, card.Id);
        }
    }

    /// <summary>Opens a random movie or episode of the library (the first pick of a server-side shuffle).</summary>
    [RelayCommand]
    private async Task ShuffleAsync()
    {
        if (_server is not { } server || _library is not { } library)
        {
            return;
        }
        ShuffleMessage = "";
        var token = _run.Token;
        try
        {
            var shuffle = await _pages.StartLibraryShuffleAsync(server.BaseUri, library.Id, token);
            if (token.IsCancellationRequested || !_isActive)
            {
                return;
            }
            if (shuffle.Current is { ContentId.Length: > 0 } pick)
            {
                _navigation.NavigateTo(AppRoute.Item, pick.ContentId);
            }
            else
            {
                ShuffleMessage = ShuffleNothingPlayable;
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (token.IsCancellationRequested)
            {
                // Another library took over.
                return;
            }
            if (ex is SiloApiException { Status: 409 })
            {
                ShuffleMessage = ShuffleNothingPlayable;
                return;
            }
            _logger.LogWarning(ex, "Could not start a shuffle of a library");
            ShuffleMessage = ShuffleFailed;
        }
    }

    private (string? ServerId, string? ProfileId, string? LibraryId) Key(string? libraryId) =>
        (_session.ActiveServer?.Id, _session.ProfileId, libraryId);

    private async Task LoadAsync(string? libraryId)
    {
        var token = Restart(TabFor(libraryId));
        LibraryId = libraryId;
        _loaded = Key(libraryId);
        _loadedAt = _time.GetUtcNow();
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            // The shell leaves the page for sign-in or the profile picker.
            State = LibraryLoadState.Idle;
            return;
        }
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            State = LibraryLoadState.Unavailable;
            return;
        }
        State = LibraryLoadState.Loading;
        try
        {
            var libraries = await _libraries.ListAsync(server.BaseUri, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            // One the profile hid is not there for it, as on the web, whatever link led here.
            if (libraries.FirstOrDefault(l => l.Id == libraryId) is not { } library)
            {
                State = LibraryLoadState.Unavailable;
                return;
            }
            await ShowAsync(server, library, token);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (token.IsCancellationRequested)
            {
                // Another library, server or profile took over.
                return;
            }
            _logger.LogWarning(ex, "Could not load the libraries to open one");
            ErrorMessage = LibraryErrors.Describe(ex);
            State = LibraryLoadState.Error;
        }
    }

    private async Task ShowAsync(ServerEntry server, Library library, CancellationToken cancellationToken)
    {
        _server = server;
        _library = library;
        _sizes = ImageSizes.Default.ScaledBy(DisplayScale > 0 && double.IsFinite(DisplayScale) ? DisplayScale : 1);
        LibraryName = library.Name ?? "";
        Browse.Initialize(server, library, _sizes);
        Collections.Initialize(server, library, _sizes);
        State = LibraryLoadState.Ready;
        await Task.WhenAll(StartTabAsync(SelectedTab, cancellationToken), UpdateShuffleAsync(server, library, cancellationToken));
    }

    // A retry or a refresh keeps the tab. Otherwise the library opens on the tab it was last left on for this server and
    // profile; one never left keeps the tab when only the server or profile changed, else starts on Recommended.
    private LibraryTab TabFor(string? libraryId)
    {
        if (_loaded == Key(libraryId))
        {
            return SelectedTab;
        }
        if (_tabs is not null && _session.ActiveServer?.Id is { } server && !string.IsNullOrWhiteSpace(libraryId)
            && _tabs.Recall(server, _session.ProfileId, libraryId) is { } remembered)
        {
            return remembered;
        }
        return libraryId == LibraryId ? SelectedTab : LibraryTab.Recommended;
    }

    private Task StartTabAsync(LibraryTab tab, CancellationToken cancellationToken) => tab switch
    {
        LibraryTab.Library => Browse.EnsureStartedAsync(),
        LibraryTab.Collections => Collections.EnsureStartedAsync(),
        _ => Recommended.State == LibraryLoadState.Idle ? LoadRowsAsync(cancellationToken) : Task.CompletedTask,
    };

    // The rows (Continue Watching, Recently Watched, …) follow the viewer's state: after a change made since they loaded,
    // a visit loads them again. The grid and the collections keep their place.
    private Task RefreshRowsIfStaleAsync()
    {
        if (State != LibraryLoadState.Ready || Recommended.State == LibraryLoadState.Idle || _rowsVersion == (_changes?.Version ?? 0))
        {
            return Task.CompletedTask;
        }
        Recommended.Reset();
        return SelectedTab == LibraryTab.Recommended ? LoadRowsAsync(_run.Token) : Task.CompletedTask;
    }

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        if (_server is not { } server || _library is not { } library)
        {
            return;
        }
        _rowsVersion = _changes?.Version ?? 0;
        ImageParameters images;
        try
        {
            images = await _images.GetAsync(server, _sizes, cancellationToken);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            images = ImageParameters.None;
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            await Recommended.LoadAsync(server.BaseUri, library.Id, images);
        }
    }

    private async Task UpdateShuffleAsync(ServerEntry server, Library library, CancellationToken cancellationToken)
    {
        if (!ShuffleLibraryTypes.Contains((library.Type ?? "").Trim()))
        {
            return;
        }
        var key = (server.Id, _session.ProfileId);
        if (!_serverCanShuffle.TryGetValue(key, out bool canShuffle))
        {
            try
            {
                var capability = await _pages.GetShuffleCapabilityAsync(server.BaseUri, cancellationToken);
                canShuffle = capability.CanShuffle(LibraryPageApi.LibraryScope);
                _serverCanShuffle[key] = canShuffle;
            }
            catch (Exception ex) when (LibraryErrors.IsExpected(ex))
            {
                // Shuffle stays hidden; the next visit asks again.
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Could not read whether the server can shuffle");
                }
                return;
            }
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            CanShuffle = canShuffle;
        }
    }

    private CancellationToken Restart(LibraryTab tab)
    {
        // Replaced before it is cancelled: continuations that resume inside Cancel already see the new run.
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        _server = null;
        _library = null;
        LibraryName = "";
        ErrorMessage = "";
        ShuffleMessage = "";
        CanShuffle = false;
        SelectedTab = tab;
        Recommended.Reset();
        Browse.Clear();
        Collections.Clear();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return _run.Token;
    }

    private void OnSessionChanged(object? sender, EventArgs e) => _dispatcher.Post(() => _ = ReloadForSessionAsync());

    // Account updates also raise Changed; only another server or profile reloads.
    private async Task ReloadForSessionAsync()
    {
        if (!_isActive || _loaded is not { } loaded || loaded == Key(loaded.LibraryId))
        {
            return;
        }
        try
        {
            await LoadAsync(loaded.LibraryId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not reload a library after the session changed");
        }
    }
}
