using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Servers;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>A shelf of collection posters on the Collections tab; the collections outside every group have no title.</summary>
/// <param name="OpenCommand">Takes the collection's <see cref="MediaCard"/>.</param>
public sealed record LibraryCollectionShelf(string? Title, IReadOnlyList<MediaCard> Cards, ICommand OpenCommand)
{
    public bool HasTitle => !string.IsNullOrEmpty(Title);
}

/// <summary>
/// The Collections tab: the library's curated collections and the viewer's opted-in personal ones, in their
/// groups (<c>GET /api/v2/library/{id}/collections</c>). A collection opens inside the tab as a paged poster
/// grid of its titles in the collection's own order.
/// </summary>
public sealed partial class LibraryCollectionsViewModel : ObservableObject
{
    /// <summary>The <see cref="MediaCard.Type"/> of a collection card.</summary>
    public const string CardType = "collection";

    private readonly LibraryPageApi _pages;
    private readonly ImageParameterCache _images;
    private readonly ILogger _logger;
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private ServerEntry? _server;
    private Library? _library;
    private ImageSizes _sizes = ImageSizes.Default;
    private bool _isStarted;
    private CancellationTokenSource _run = new();

    public LibraryCollectionsViewModel(LibraryPageApi pages, CatalogApi catalog, ImageParameterCache images, ILogger logger)
    {
        _pages = pages;
        _images = images;
        _logger = logger;
        Items = new LibraryGridViewModel(catalog, logger);
    }

    public ObservableCollection<LibraryCollectionShelf> Shelves { get; } = [];

    /// <summary>The titles of the open collection.</summary>
    public LibraryGridViewModel Items { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(HasError))]
    public partial LibraryLoadState State { get; private set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; private set; } = "";

    /// <summary>The collection whose titles show, or null for the shelves.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollectionOpen), nameof(IsShelvesShown), nameof(OpenCollectionTitle))]
    public partial MediaCard? OpenCollection { get; private set; }

    public bool IsCollectionOpen => OpenCollection is not null;

    public bool IsShelvesShown => OpenCollection is null;

    public string OpenCollectionTitle => OpenCollection?.Title ?? "";

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsEmpty => State == LibraryLoadState.Empty;

    public bool HasError => State == LibraryLoadState.Error;

    /// <summary>Points the tab at a library without loading anything.</summary>
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

    /// <summary>Forgets the library and stops its loads.</summary>
    public void Clear()
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        _server = null;
        _library = null;
        _isStarted = false;
        _sources.Clear();
        Shelves.Clear();
        OpenCollection = null;
        Items.Reset();
        ErrorMessage = "";
        State = LibraryLoadState.Idle;
    }

    /// <summary>Loads the shelves the first time the tab is shown.</summary>
    public Task EnsureStartedAsync() => _isStarted ? Task.CompletedTask : LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_server is not { } server || _library is not { } library)
        {
            return;
        }
        _isStarted = true;
        var token = _run.Token;
        Shelves.Clear();
        _sources.Clear();
        State = LibraryLoadState.Loading;
        try
        {
            var tab = await _pages.GetCollectionsAsync(server.BaseUri, library.Id, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            foreach (var shelf in ShelvesOf(tab, server.BaseUri))
            {
                Shelves.Add(shelf);
            }
            State = Shelves.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (token.IsCancellationRequested)
            {
                // Another library took over.
                return;
            }
            _logger.LogWarning(ex, "Could not load the collections of a library");
            ErrorMessage = LibraryErrors.Describe(ex);
            State = LibraryLoadState.Error;
        }
    }

    /// <summary>Shows the titles of a collection card from <see cref="Shelves"/>.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenAsync(MediaCard? collection)
    {
        if (collection is null || _server is not { } server || _library is not { } library
            || !_sources.TryGetValue(collection.Id, out string? source))
        {
            return;
        }
        OpenCollection = collection;
        var token = _run.Token;
        ImageParameters images;
        try
        {
            images = await _images.GetAsync(server, _sizes, token);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            images = ImageParameters.None;
        }
        if (token.IsCancellationRequested || OpenCollection != collection)
        {
            return;
        }
        await Items.StartAsync(server.BaseUri, new CatalogQuery
        {
            Source = source,
            CollectionId = collection.Id,
            // Personal collections are not bound to a library.
            LibraryId = source == CatalogSources.LibraryCollection ? library.Id : null,
            Limit = LibraryBrowseViewModel.PageSize,
            Images = images,
        });
    }

    /// <summary>Back from a collection's titles to the shelves.</summary>
    [RelayCommand]
    private void Close()
    {
        OpenCollection = null;
        Items.Reset();
    }

    private List<LibraryCollectionShelf> ShelvesOf(LibraryCollectionTab tab, Uri server)
    {
        var slots = new List<(int Order, string? Title, string Source, IReadOnlyList<LibraryCollectionCard> Cards)>();
        foreach (var group in tab.Groups)
        {
            string source = group.Kind == LibraryCollectionGroupKinds.UserCollections ? CatalogSources.UserCollection : CatalogSources.LibraryCollection;
            slots.Add((group.SortOrder, group.Name, source, group.Collections));
        }
        if (tab.Ungrouped is { } ungrouped)
        {
            slots.Add((ungrouped.SortOrder, null, CatalogSources.LibraryCollection, ungrouped.Collections));
        }
        if (slots.Count == 0 && tab.Collections.Count > 0)
        {
            // A server without collection groups lists its collections only in full.
            var cards = tab.Collections.OrderBy(c => c.SortOrder)
                .Select(c => new LibraryCollectionCard { Id = c.Id, Title = c.Title, PosterUrl = c.PosterUrl, ItemCount = c.ItemCount })
                .ToList();
            slots.Add((0, null, CatalogSources.LibraryCollection, cards));
        }
        // A card without an id could not be opened.
        return [.. slots
            .OrderBy(s => s.Order)
            .Select(s => (s.Title, Cards: s.Cards.Where(c => !string.IsNullOrWhiteSpace(c.Id)).Select(c => CardOf(c, s.Source, server)).ToList()))
            .Where(s => s.Cards.Count > 0)
            .Select(s => new LibraryCollectionShelf(s.Title, s.Cards, OpenCommand))];
    }

    private MediaCard CardOf(LibraryCollectionCard collection, string source, Uri server)
    {
        _sources[collection.Id] = source;
        string count = collection.ItemCount.ToString("N0", CultureInfo.InvariantCulture);
        return new MediaCard(collection.Id, CardType, collection.Title)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = collection.ItemCount == 1 ? "1 TITLE" : $"{count} TITLES",
            ImageUrl = ArtworkUrl.Resolve(server, collection.PosterUrl),
        };
    }
}
