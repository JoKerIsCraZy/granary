using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>A media scope the personal lists narrow to, as the web's media menu (<c>CatalogFilterBar</c>).</summary>
/// <param name="Type">The browse <c>type</c>; null for every kind.</param>
public sealed record ListMediaScope(string? Type, string Label)
{
    public static IReadOnlyList<ListMediaScope> All { get; } =
    [
        new(null, "All Media"),
        new(MediaScopes.Video, "Movies & Series"),
        new(MediaScopes.Movie, "Movies"),
        new(MediaScopes.Series, "Series"),
        new(MediaScopes.Episode, "Episodes"),
    ];
}

/// <summary>
/// Favorites, Watchlist, History or a collection: a list in a poster grid, as Silo's web catalog page shows it for
/// those sources (<c>pages/Catalog.tsx</c>). The list's own order comes first ("List Order", History's "Watch History",
/// a collection's "Collection Order"); the other sorts and a media scope narrow it. The grid loads by window. A visit
/// after the viewer's state changed (<see cref="ViewerStateChanges"/>: a favourite, a watched mark, playback) loads it
/// again. The watchlist also shows "Not in your library yet" (<see cref="Titles"/>) as a second tab, while the server keeps
/// titles the library does not have. On History the viewer can pick titles and clear their watch data (Selection).
/// </summary>
public sealed partial class PersonalListViewModel : ObservableObject
{
    public const int PageSize = 60;

    private static readonly Dictionary<string, (string Title, string Description, string OrderLabel)> Lists = new(StringComparer.Ordinal)
    {
        [CatalogSources.Favorites] = ("Favorites", "Movies and shows you've marked as favorites.", "List Order"),
        [CatalogSources.Watchlist] = ("Watchlist", "Things you've saved to watch later.", "List Order"),
        [CatalogSources.History] = ("History", "Everything you've recently watched.", "Watch History"),
    };

    // The web's catalog page says the same over every other source.
    private const string CollectionDescription = "Refine the archive by type, era, rating, or genre.";
    private const string CollectionOrder = "Collection Order";

    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly INavigationService _navigation;
    private readonly ILogger<PersonalListViewModel> _logger;
    private readonly ViewerStateChanges? _changes;
    private (string? ServerId, string? ProfileId, string Source, CollectionRouteParameter? Collection)? _loadedFor;
    private CollectionRouteParameter? _collection;
    private long _loadedVersion;
    private int _starts;

    public PersonalListViewModel(ISessionManager session, CatalogApi catalog, ImageParameterCache images, INavigationService navigation,
        WatchlistTitlesViewModel titles, ILogger<PersonalListViewModel> logger, ViewerStateChanges? changes = null)
    {
        _session = session;
        _catalog = catalog;
        _images = images;
        _navigation = navigation;
        _logger = logger;
        _changes = changes;
        Grid = new LibraryGridViewModel(catalog, logger);
        Titles = titles;
        Titles.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WatchlistTitlesViewModel.IsAvailable))
            {
                OnPropertyChanged(nameof(ShowsTabs));
                OnPropertyChanged(nameof(ShowsTitles));
                OnPropertyChanged(nameof(ShowsLibrary));
            }
        };
        // A title that left "Not in your library yet" may be in the library's watchlist now.
        Titles.TitlesLeft += (_, _) =>
        {
            if (Source == CatalogSources.Watchlist && _collection is null)
            {
                _ = StartAsync();
            }
        };
    }

    public LibraryGridViewModel Grid { get; }

    /// <summary>The watchlist's titles the library does not have yet, its second tab.</summary>
    public WatchlistTitlesViewModel Titles { get; }

    /// <summary>
    /// <see cref="CatalogSources.Favorites"/>, <see cref="CatalogSources.Watchlist"/>, <see cref="CatalogSources.History"/>,
    /// or a collection's <see cref="CatalogSources.UserCollection"/> or <see cref="CatalogSources.LibraryCollection"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTabs), nameof(ShowsTitles), nameof(ShowsLibrary))]
    public partial string Source { get; private set; } = "";

    /// <summary>The watchlist's "Not in your library yet" tab is picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTitles), nameof(ShowsLibrary))]
    public partial bool IsTitlesTab { get; private set; }

    /// <summary>"In your library" and "Not in your library yet" over the watchlist, while the server keeps such titles.</summary>
    public bool ShowsTabs => Source == CatalogSources.Watchlist && _collection is null && Titles.IsAvailable;

    public bool ShowsTitles => ShowsTabs && IsTitlesTab;

    /// <summary>The list's count, menus and posters: everywhere but on the watchlist's second tab.</summary>
    public bool ShowsLibrary => !ShowsTitles;

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial string Description { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeLabel))]
    public partial ListMediaScope Scope { get; private set; } = ListMediaScope.All[0];

    /// <summary>The picked sort; null for the list's own order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel), nameof(IsOrderShown))]
    public partial LibrarySortOption? Sort { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrderLabel))]
    public partial bool IsDescending { get; private set; }

    /// <summary>The display's scale (1.5 at 150 %), so artwork is asked for at the pixels it is drawn at.</summary>
    public double DisplayScale { get; set; } = 1;

    public IReadOnlyList<ListMediaScope> Scopes => ListMediaScope.All;

    public IReadOnlyList<LibrarySortOption> SortOptions => LibrarySortOption.All;

    /// <summary>"List Order", "Watch History" for History, or "Collection Order": the list's own order.</summary>
    public string SourceOrderLabel => _collection is not null ? CollectionOrder
        : Lists.TryGetValue(Source, out var list) ? list.OrderLabel : "List Order";

    public string ScopeLabel => Scope.Label;

    public string SortLabel => Sort?.Label ?? SourceOrderLabel;

    public string OrderLabel => IsDescending ? "Descending" : "Ascending";

    /// <summary>The list's own order has no direction to pick.</summary>
    public bool IsOrderShown => Sort is not null;

    /// <summary>
    /// Shows the list named by <paramref name="source"/>: on every visit of the page. The same list again keeps its grid and
    /// place, unless another profile shows or the viewer's state changed since it loaded.
    /// </summary>
    public Task ActivateAsync(string? source)
    {
        if (source is null || !Lists.TryGetValue(source, out var list))
        {
            return Task.CompletedTask;
        }
        if (source != Source || _collection is not null)
        {
            Show(source, list.Title, list.Description, collection: null);
        }
        if (source == CatalogSources.Watchlist)
        {
            return Task.WhenAll(LoadUnlessCurrentAsync(), Titles.ActivateAsync());
        }
        Titles.Deactivate();
        return LoadUnlessCurrentAsync();
    }

    /// <summary>The page left: the watchlist's titles stop following their downloads.</summary>
    public void Deactivate() => Titles.Deactivate();

    /// <summary>Shows a collection's titles, as <see cref="ActivateAsync"/> a list's.</summary>
    public Task ShowCollectionAsync(CollectionRouteParameter collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection != _collection)
        {
            Show(collection.Source, collection.Title, CollectionDescription, collection);
        }
        Titles.Deactivate();
        return LoadUnlessCurrentAsync();
    }

    private void Show(string source, string title, string description, CollectionRouteParameter? collection)
    {
        _collection = collection;
        Source = source;
        LeaveHistory();
        IsTitlesTab = false;
        Title = title;
        Description = description;
        Scope = ListMediaScope.All[0];
        Sort = null;
        IsDescending = false;
        OnPropertyChanged(nameof(SourceOrderLabel));
        OnPropertyChanged(nameof(SortLabel));
        // The other list's cards must not show under this one's name while its first page is on its way.
        Grid.Reset();
    }

    private Task LoadUnlessCurrentAsync()
    {
        bool current = _loadedFor == Key() && _loadedVersion == (_changes?.Version ?? 0)
            && Grid.State is LibraryLoadState.Ready or LibraryLoadState.Empty or LibraryLoadState.Loading;
        return current ? Task.CompletedTask : StartAsync();
    }

    [RelayCommand]
    private Task SetScopeAsync(ListMediaScope? scope)
    {
        if (scope is null || scope == Scope)
        {
            return Task.CompletedTask;
        }
        Scope = scope;
        return StartAsync();
    }

    /// <summary>Sorts by <paramref name="option"/>, in the order it starts in; null goes back to the list's own order.</summary>
    [RelayCommand]
    private Task SetSortAsync(LibrarySortOption? option)
    {
        if (option == Sort)
        {
            return Task.CompletedTask;
        }
        Sort = option;
        IsDescending = option?.StartsDescending ?? false;
        return StartAsync();
    }

    [RelayCommand]
    private void ShowLibraryTab() => IsTitlesTab = false;

    [RelayCommand]
    private void ShowTitlesTab() => IsTitlesTab = true;

    [RelayCommand]
    private Task SortAscendingAsync() => SetOrderAsync(descending: false);

    [RelayCommand]
    private Task SortDescendingAsync() => SetOrderAsync(descending: true);

    [RelayCommand]
    private void OpenItem(MediaCard? card)
    {
        if (card is { IsPlaceholder: false })
        {
            _navigation.NavigateTo(AppRoute.Item, card.Id);
        }
    }

    private Task SetOrderAsync(bool descending)
    {
        if (Sort is null || descending == IsDescending)
        {
            return Task.CompletedTask;
        }
        IsDescending = descending;
        return StartAsync();
    }

    private async Task StartAsync()
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server } || Source.Length == 0)
        {
            Grid.Reset();
            return;
        }
        int start = ++_starts;
        _loadedFor = Key();
        // Read before asking: a change while the answer is on its way leaves this list stale.
        _loadedVersion = _changes?.Version ?? 0;
        var images = await BrowseArtwork.ParametersAsync(_images, server, DisplayScale, _logger, CancellationToken.None);
        if (start != _starts)
        {
            // Another scope or sort was picked meanwhile; its own start shows it.
            return;
        }
        await Grid.StartAsync(server.BaseUri, new CatalogQuery
        {
            Source = Source,
            CollectionId = _collection?.CollectionId,
            LibraryId = _collection?.LibraryId,
            Type = Scope.Type,
            Sort = Sort is { } sort ? new CatalogSort(sort.Field, IsDescending) : null,
            Limit = PageSize,
            Images = images,
        });
    }

    private (string? ServerId, string? ProfileId, string Source, CollectionRouteParameter? Collection) Key() =>
        (_session.ActiveServer?.Id, _session.ProfileId, Source, _collection);
}
