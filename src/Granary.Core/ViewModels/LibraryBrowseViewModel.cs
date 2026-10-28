using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Servers;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// The Library tab: every title of the library in a poster grid, with sort, order, filters and the A–Z bar.
/// Each change starts the listing over with fresh image parameters, so the new cursors are bound to them. In title
/// order the letter index tells where each letter starts: a letter scrolls the grid there, as on Silo's web client,
/// and the bar marks the letter of the top row.
/// </summary>
public sealed partial class LibraryBrowseViewModel : ObservableObject
{
    /// <summary>Titles per page, as the web client asks for.</summary>
    public const int PageSize = 60;

    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly ILogger _logger;
    private ServerEntry? _server;
    private Library? _library;
    private ImageSizes _sizes = ImageSizes.Default;
    private bool _isStarted;
    private int _restarts;
    private string? _jumpedLetter;
    private int _topRowEnd;
    private (Uri Server, CatalogQuery Query)? _indexed;
    private CancellationTokenSource _scope = new();

    public LibraryBrowseViewModel(CatalogApi catalog, ImageParameterCache images, ILogger logger)
    {
        _catalog = catalog;
        _images = images;
        _logger = logger;
        Grid = new LibraryGridViewModel(catalog, logger);
        Grid.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryGridViewModel.State))
            {
                OnPropertyChanged(nameof(IsLetterBarVisible));
            }
        };
        Grid.ListingChanged += (_, _) => _ = ReloadLettersAsync();
        Filters = new LibraryFiltersViewModel(() => Task.WhenAll(RememberAsync(), RestartAsync()), logger);
    }

    /// <summary>Keeps the sort and filters each time they change (the library page's memory); null keeps nothing.</summary>
    public Func<LibraryBrowseState, Task>? Remember { get; set; }

    /// <summary>The sort and filters as they are now.</summary>
    public LibraryBrowseState CurrentState =>
        new(Sort.Field, IsDescending, Filters.Genre, Filters.ContentRatings, Filters.YearFrom, Filters.YearTo);

    public LibraryGridViewModel Grid { get; }

    public LibraryFiltersViewModel Filters { get; }

    public IReadOnlyList<LibrarySortOption> SortOptions => LibrarySortOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel), nameof(IsLetterBarVisible))]
    public partial LibrarySortOption Sort { get; private set; } = LibrarySortOption.Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrderLabel))]
    public partial bool IsDescending { get; private set; }

    /// <summary>The letter index of the listing in title order; null while it loads, in other orders, or when it failed.</summary>
    [ObservableProperty]
    public partial CatalogLetters? Letters { get; private set; }

    /// <summary>The letter the top row of the grid is under, or the one just jumped to while the grid is at its end.</summary>
    [ObservableProperty]
    public partial string? ActiveLetter { get; private set; }

    /// <summary>The letter index could not be read: the bar steps aside.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLetterBarVisible))]
    public partial bool HasLettersError { get; private set; }

    public string SortLabel => Sort.Label;

    public string OrderLabel => IsDescending ? "Descending" : "Ascending";

    /// <summary>
    /// The A–Z bar holds its place while titles are in title order, its letters greyed until the index arrives; it steps
    /// aside when nothing is listed or the index cannot be read.
    /// </summary>
    public bool IsLetterBarVisible => Sort.IsTitle && !HasLettersError && !Grid.IsEmpty && !Grid.HasError;

    /// <summary>Filters narrow the listing; an empty result then offers to clear them.</summary>
    public bool IsNarrowed => Filters.HasActiveFilters;

    /// <summary>Asks the view to scroll the grid so the title at this list position is in the top row.</summary>
    public event EventHandler<int>? JumpRequested;

    /// <summary>Points the tab at a library in the default order (title, A to Z) without loading anything.</summary>
    public void Initialize(ServerEntry server, Library library, ImageSizes sizes)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(sizes);
        Clear();
        _server = server;
        _library = library;
        _sizes = sizes;
    }

    /// <summary>Forgets the library: no request of it is answered any more.</summary>
    public void Clear()
    {
        var previous = _scope;
        _scope = new CancellationTokenSource();
        _restarts++;
        previous.Cancel();
        previous.Dispose();
        _server = null;
        _library = null;
        _isStarted = false;
        Sort = LibrarySortOption.Title;
        IsDescending = false;
        ClearLetters();
        _indexed = null;
        Filters.Reset();
        Grid.Reset();
        OnPropertyChanged(nameof(IsNarrowed));
    }

    /// <summary>
    /// Sorts and filters as <paramref name="state"/> says, before the first page loads; a sort this app does not know
    /// keeps the title order.
    /// </summary>
    public void Restore(LibraryBrowseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var known = LibrarySortOption.All.FirstOrDefault(o => o.Field == state.SortField);
        Sort = known ?? LibrarySortOption.Title;
        IsDescending = known is not null && state.Descending;
        Filters.Restore(state.Genre, state.ContentRatings, state.YearFrom, state.YearTo);
        OnPropertyChanged(nameof(IsNarrowed));
    }

    /// <summary>Loads the first page and the filter options the first time the tab is shown.</summary>
    public async Task EnsureStartedAsync()
    {
        if (_isStarted || _server is not { } server || _library is not { } library)
        {
            return;
        }
        _isStarted = true;
        var options = Filters.LoadOptionsAsync(_catalog, server.BaseUri, CatalogQuery.ForLibrary(library), _scope.Token);
        await Task.WhenAll(RestartAsync(), options);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SetSortAsync(LibrarySortOption? option)
    {
        if (option is null || option == Sort)
        {
            return Task.CompletedTask;
        }
        Sort = option;
        IsDescending = option.StartsDescending;
        return Task.WhenAll(RememberAsync(), RestartAsync());
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SortAscendingAsync() => SetOrderAsync(descending: false);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SortDescendingAsync() => SetOrderAsync(descending: true);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleOrderAsync() => SetOrderAsync(!IsDescending);

    /// <summary>
    /// Scrolls the grid to the first title under <paramref name="letter"/> ("A" … "Z" or "#"), which becomes the
    /// marked letter; a letter without titles, or one picked before the index arrived, does nothing.
    /// </summary>
    [RelayCommand]
    private void JumpToLetter(string? letter)
    {
        if (!Sort.IsTitle || Letters is not { } letters || letters.Letters.FirstOrDefault(l => l.Letter == letter) is not { } target)
        {
            return;
        }
        _jumpedLetter = target.Letter;
        ActiveLetter = target.Letter;
        JumpRequested?.Invoke(this, target.Position);
    }

    /// <summary>
    /// Loads the titles in view and marks the letter of the top row. A letter near the end cannot scroll to the top, so
    /// one just jumped to stays marked while the grid is at its end; any scroll away hands back to the top row.
    /// </summary>
    [RelayCommand]
    private void ShowViewport(GridViewport? viewport)
    {
        if (viewport is null)
        {
            return;
        }
        Grid.ShowViewport(viewport);
        if (_jumpedLetter is not null && viewport.IsAtEnd)
        {
            return;
        }
        _jumpedLetter = null;
        _topRowEnd = viewport.TopRowEnd;
        ActiveLetter = Letters?.LetterAt(_topRowEnd);
    }

    /// <summary>Clears the filters, from the empty state.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ClearFiltersAsync()
    {
        Filters.ClearSilently();
        return Task.WhenAll(RememberAsync(), RestartAsync());
    }

    private Task SetOrderAsync(bool descending)
    {
        if (descending == IsDescending)
        {
            return Task.CompletedTask;
        }
        IsDescending = descending;
        return Task.WhenAll(RememberAsync(), RestartAsync());
    }

    private Task RememberAsync() => _library is null || Remember is not { } remember ? Task.CompletedTask : remember(CurrentState);

    private async Task RestartAsync()
    {
        if (_server is not { } server || _library is not { } library)
        {
            return;
        }
        _isStarted = true;
        int restart = ++_restarts;
        OnPropertyChanged(nameof(IsNarrowed));
        ClearLetters();
        var token = _scope.Token;
        ImageParameters images;
        try
        {
            images = await _images.GetAsync(server, _sizes, token);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            images = ImageParameters.None;
        }
        // A newer change started its own listing while this one waited, or the library was cleared.
        if (restart != _restarts || token.IsCancellationRequested)
        {
            return;
        }
        var query = BuildQuery(library, images);
        _indexed = Sort.IsTitle ? (server.BaseUri, query) : null;
        var letters = _indexed is { } indexed ? LoadLettersAsync(indexed.Server, indexed.Query, restart, token) : Task.CompletedTask;
        await Task.WhenAll(Grid.StartAsync(server.BaseUri, query), letters);
    }

    // The listing changed under the grid's cursor and the grid started over: the letters' positions changed with it.
    private Task ReloadLettersAsync()
    {
        if (_indexed is not { } indexed)
        {
            return Task.CompletedTask;
        }
        ClearLetters();
        return LoadLettersAsync(indexed.Server, indexed.Query, _restarts, _scope.Token);
    }

    private async Task LoadLettersAsync(Uri server, CatalogQuery query, int restart, CancellationToken cancellationToken)
    {
        try
        {
            var letters = await _catalog.GetLettersAsync(server, query, cancellationToken);
            if (restart == _restarts && !cancellationToken.IsCancellationRequested)
            {
                Letters = letters;
                ActiveLetter ??= letters.LetterAt(_topRowEnd);
            }
        }
        catch (Exception ex)
        {
            // Also when reloaded unawaited: nothing it throws may go unobserved.
            if (restart == _restarts && !cancellationToken.IsCancellationRequested)
            {
                _logger.Log(LibraryErrors.IsExpected(ex) ? LogLevel.Warning : LogLevel.Error, ex, "Could not load the letter index of a library");
                HasLettersError = true;
            }
        }
    }

    // A new listing starts at its top.
    private void ClearLetters()
    {
        _jumpedLetter = null;
        _topRowEnd = 0;
        Letters = null;
        ActiveLetter = null;
        HasLettersError = false;
    }

    private CatalogQuery BuildQuery(Library library, ImageParameters images) =>
        Filters.ApplyTo(CatalogQuery.ForLibrary(library)) with
        {
            Sort = new CatalogSort(Sort.Field, IsDescending),
            Limit = PageSize,
            Images = images,
        };
}
