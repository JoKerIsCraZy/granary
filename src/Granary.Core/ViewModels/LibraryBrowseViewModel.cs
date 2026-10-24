using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// The Library tab: every title of the library in a poster grid, with sort, order, filters and the A–Z bar.
/// Each change starts the listing over with fresh image parameters, so the new cursors are bound to them.
/// </summary>
public sealed partial class LibraryBrowseViewModel : ObservableObject
{
    /// <summary>Titles per page, as the web client asks for.</summary>
    public const int PageSize = 60;

    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private ServerEntry? _server;
    private Library? _library;
    private ImageSizes _sizes = ImageSizes.Default;
    private bool _isStarted;
    private int _restarts;
    private CancellationTokenSource _scope = new();

    public LibraryBrowseViewModel(CatalogApi catalog, ImageParameterCache images, ILogger logger)
    {
        _catalog = catalog;
        _images = images;
        Grid = new LibraryGridViewModel(catalog, logger);
        Filters = new LibraryFiltersViewModel(RestartAsync, logger);
    }

    public LibraryGridViewModel Grid { get; }

    public LibraryFiltersViewModel Filters { get; }

    public IReadOnlyList<LibrarySortOption> SortOptions => LibrarySortOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel), nameof(IsLetterBarVisible))]
    public partial LibrarySortOption Sort { get; private set; } = LibrarySortOption.Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrderLabel))]
    public partial bool IsDescending { get; private set; }

    /// <summary>The letter picked on the A–Z bar, or null for every title.</summary>
    [ObservableProperty]
    public partial string? NamePrefix { get; private set; }

    public string SortLabel => Sort.Label;

    public string OrderLabel => IsDescending ? "Descending" : "Ascending";

    /// <summary>The A–Z bar shows only while titles are in title order.</summary>
    public bool IsLetterBarVisible => Sort.IsTitle;

    /// <summary>Filters or a letter narrow the listing; an empty result then offers to clear them.</summary>
    public bool IsNarrowed => Filters.HasActiveFilters || NamePrefix is not null;

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
        NamePrefix = null;
        Filters.Reset();
        Grid.Reset();
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
        if (!option.IsTitle)
        {
            // The bar is hidden in other orders; a letter it no longer shows must not narrow the grid.
            NamePrefix = null;
        }
        return RestartAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SortAscendingAsync() => SetOrderAsync(descending: false);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SortDescendingAsync() => SetOrderAsync(descending: true);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ToggleOrderAsync() => SetOrderAsync(!IsDescending);

    /// <summary>
    /// Shows the titles whose sort title starts with <paramref name="letter"/> (A–Z); null, "" or "#" shows every
    /// title, and so does the picked letter again. The server matches <c>name_prefix</c> literally and has no
    /// bucket for digits and symbols, so "#" cannot narrow the grid.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SelectLetterAsync(string? letter)
    {
        if (!Sort.IsTitle)
        {
            return Task.CompletedTask;
        }
        string? prefix;
        if (string.IsNullOrWhiteSpace(letter) || letter.Trim() == "#")
        {
            prefix = null;
        }
        else if (letter.Trim() is [var single] && char.IsAsciiLetter(single))
        {
            prefix = char.ToUpperInvariant(single).ToString();
        }
        else
        {
            return Task.CompletedTask;
        }
        if (prefix is not null && prefix == NamePrefix)
        {
            prefix = null;
        }
        if (prefix == NamePrefix)
        {
            return Task.CompletedTask;
        }
        NamePrefix = prefix;
        return RestartAsync();
    }

    /// <summary>Clears the filters and the letter together, from the empty state.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ClearFiltersAsync()
    {
        Filters.ClearSilently();
        NamePrefix = null;
        return RestartAsync();
    }

    private Task SetOrderAsync(bool descending)
    {
        if (descending == IsDescending)
        {
            return Task.CompletedTask;
        }
        IsDescending = descending;
        return RestartAsync();
    }

    private async Task RestartAsync()
    {
        if (_server is not { } server || _library is not { } library)
        {
            return;
        }
        _isStarted = true;
        int restart = ++_restarts;
        OnPropertyChanged(nameof(IsNarrowed));
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
        await Grid.StartAsync(server.BaseUri, BuildQuery(library, images));
    }

    private CatalogQuery BuildQuery(Library library, ImageParameters images) =>
        Filters.ApplyTo(CatalogQuery.ForLibrary(library)) with
        {
            Sort = new CatalogSort(Sort.Field, IsDescending),
            NamePrefix = Sort.IsTitle ? NamePrefix : null,
            Limit = PageSize,
            Images = images,
        };
}
