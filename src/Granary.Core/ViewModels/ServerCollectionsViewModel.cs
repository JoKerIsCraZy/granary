using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// The Collections page's server collections, as the web's <c>ServerCollectionsSection</c>: a pill per library when
/// there are several (All first), and one poster grid. <c>GET /api/v2/collections/server</c> carries only each
/// library's first few; the rest come from the libraries' Collections tabs, which list the same collections in the
/// same order. A picked library loads its rest straight away. All, or a lone library, loads one library's rest at a
/// time, each time the end of the grid comes into view or Load more is pressed. Every library's first few come
/// before any library's rest, and each collection shows once: one in several libraries opens across all of them.
/// </summary>
public sealed partial class ServerCollectionsViewModel : ObservableObject
{
    public const string AllLibraries = "all";

    private static readonly SettingChoice All = new(AllLibraries, "All");

    private readonly LibraryPageApi _pages;
    private readonly INavigationService _navigation;
    private readonly ILogger _logger;
    // Each library's whole list, once read.
    private readonly Dictionary<string, IReadOnlyList<LibraryCollectionCard>> _rests = new(StringComparer.Ordinal);
    // The libraries whose rest All asked for, one per end of the grid.
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    // The library each card opens in; null for a collection in several.
    private readonly Dictionary<string, string?> _libraryOf = new(StringComparer.Ordinal);
    private IReadOnlyList<ServerCollectionLibrary> _libraries = [];
    private Uri? _server;
    private CancellationTokenSource _run = new();

    public ServerCollectionsViewModel(LibraryPageApi pages, INavigationService navigation, ILogger logger)
    {
        _pages = pages;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<MediaCard> Cards { get; } = [];

    /// <summary>All, then each library; empty with one library or none.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SettingChoice> LibraryChoices { get; private set; } = [];

    [ObservableProperty]
    public partial SettingChoice SelectedLibrary { get; set; } = All;

    /// <summary>The section shows only while some library has server collections.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    /// <summary>All has a library whose rest it has not asked for yet.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool CanLoadMore { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoadingMore { get; private set; }

    /// <summary>"Couldn't load every collection in Filme.", or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    public partial string FailureText { get; private set; } = "";

    public bool HasFailure => FailureText.Length > 0;

    /// <summary>Shows a fresh answer of <c>GET /api/v2/collections/server</c>, from All.</summary>
    public void Show(Uri server, ServerCollectionList list)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(list);
        Clear();
        _server = server;
        _libraries = [.. list.Libraries.Where(l => l.LibraryId.Length > 0 && l.Collections.Count > 0)];
        LibraryChoices = _libraries.Count > 1 ? [All, .. _libraries.Select(l => new SettingChoice(l.LibraryId, l.LibraryName))] : [];
        IsShown = _libraries.Count > 0;
        Refresh();
    }

    /// <summary>Forgets the lists and stops their loads.</summary>
    public void Clear()
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        _server = null;
        _libraries = [];
        _rests.Clear();
        _requested.Clear();
        _loading.Clear();
        _failed.Clear();
        _libraryOf.Clear();
        Cards.Clear();
        LibraryChoices = [];
        SelectedLibrary = All;
        IsShown = false;
        CanLoadMore = false;
        IsLoadingMore = false;
        FailureText = "";
    }

    /// <summary>Loads the rest of the next library when the end of the grid is in view.</summary>
    [RelayCommand]
    private void ShowViewport(GridViewport? viewport)
    {
        if (viewport is not null && Cards.Count > 0 && (viewport.IsAtEnd || viewport.Last >= Cards.Count - 1) && CanLoadMore && !IsLoadingMore)
        {
            LoadMore();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPressLoadMore))]
    private void LoadMore()
    {
        if (Next() is { } next)
        {
            _requested.Add(next.LibraryId);
            Refresh();
        }
    }

    /// <summary>Tries the libraries whose whole list failed again.</summary>
    [RelayCommand]
    private void Retry()
    {
        _failed.Clear();
        Refresh();
    }

    [RelayCommand]
    private void Open(MediaCard? card)
    {
        if (card is { IsPlaceholder: false } && _libraryOf.TryGetValue(card.Id, out string? libraryId))
        {
            _navigation.NavigateTo(AppRoute.Collection, new CollectionRouteParameter(CatalogSources.LibraryCollection, card.Id, card.Title, libraryId));
        }
    }

    private bool CanPressLoadMore() => CanLoadMore && !IsLoadingMore;

    partial void OnSelectedLibraryChanged(SettingChoice value) => Refresh();

    // The libraries in view: the picked one, the lone one, or all. Only All and a lone library load on scroll.
    private IReadOnlyList<ServerCollectionLibrary> InView(out bool loadsOnScroll)
    {
        var picked = _libraries.FirstOrDefault(l => l.LibraryId == SelectedLibrary.Value);
        loadsOnScroll = picked is null;
        var library = _libraries.Count == 1 ? _libraries[0] : picked;
        return library is null ? _libraries : [library];
    }

    private bool IsAsked(ServerCollectionLibrary library, bool loadsOnScroll) =>
        library.IsCutShort && (!loadsOnScroll || _requested.Contains(library.LibraryId));

    // The library whose rest All asks for next.
    private ServerCollectionLibrary? Next()
    {
        var inView = InView(out bool loadsOnScroll);
        return loadsOnScroll ? inView.FirstOrDefault(l => l.IsCutShort && !_requested.Contains(l.LibraryId)) : null;
    }

    // Lays out the cards in view and starts the loads they wait for.
    private void Refresh()
    {
        if (_server is not { } server)
        {
            return;
        }
        var inView = InView(out bool loadsOnScroll);
        var parts = inView.Select(l => (Library: l, First: l.Collections, More: RestOf(l, loadsOnScroll))).ToList();
        // A collection in several libraries opens across all of them.
        var libraries = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, first, more) in parts)
        {
            foreach (string id in first.Concat(more).Select(c => c.Id).Distinct(StringComparer.Ordinal))
            {
                libraries[id] = libraries.GetValueOrDefault(id) + 1;
            }
        }
        _libraryOf.Clear();
        var cards = new List<MediaCard>();
        foreach (var (library, collections) in parts.Select(p => (p.Library, p.First)).Concat(parts.Select(p => (p.Library, p.More))))
        {
            foreach (var collection in collections)
            {
                // A card without an id could not be opened.
                if (string.IsNullOrWhiteSpace(collection.Id) || !_libraryOf.TryAdd(collection.Id, libraries[collection.Id] > 1 ? null : library.LibraryId))
                {
                    continue;
                }
                cards.Add(CollectionCards.Of(collection, server));
            }
        }
        Show(cards);
        CanLoadMore = Next() is not null;
        var waiting = inView.Where(l => IsAsked(l, loadsOnScroll) && !_rests.ContainsKey(l.LibraryId)
            && !_loading.Contains(l.LibraryId) && !_failed.Contains(l.LibraryId)).ToList();
        foreach (var library in waiting)
        {
            _ = LoadRestAsync(server, library);
        }
        IsLoadingMore = inView.Any(l => _loading.Contains(l.LibraryId));
        var failed = inView.Where(l => _failed.Contains(l.LibraryId)).Select(l => l.LibraryName).ToList();
        FailureText = failed.Count == 0 ? "" : $"Couldn't load every collection in {string.Join(", ", failed)}.";
    }

    // The collections a library adds to its first few, once its whole list is read and asked for.
    private IReadOnlyList<LibraryCollectionCard> RestOf(ServerCollectionLibrary library, bool loadsOnScroll)
    {
        if (!IsAsked(library, loadsOnScroll) || !_rests.TryGetValue(library.LibraryId, out var full))
        {
            return [];
        }
        var first = library.Collections.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return [.. full.Where(c => !first.Contains(c.Id))];
    }

    // Cards added among the same ones keep the grid where it is (a rest retried after a later library's); anything else
    // replaces it.
    private void Show(List<MediaCard> cards)
    {
        int kept = 0;
        foreach (var card in cards)
        {
            if (kept < Cards.Count && Cards[kept].Id == card.Id)
            {
                kept++;
            }
        }
        if (kept < Cards.Count)
        {
            Cards.Clear();
        }
        for (int i = 0; i < cards.Count; i++)
        {
            if (i >= Cards.Count || Cards[i].Id != cards[i].Id)
            {
                Cards.Insert(i, cards[i]);
            }
        }
    }

    // Started without awaiting: whatever goes wrong shows as a library that failed, which Retry asks again.
    private async Task LoadRestAsync(Uri server, ServerCollectionLibrary library)
    {
        var token = _run.Token;
        _loading.Add(library.LibraryId);
        try
        {
            var tab = await _pages.GetCollectionsAsync(server, library.LibraryId, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            _rests[library.LibraryId] = [.. tab.Collections.Select(c => new LibraryCollectionCard
            {
                Id = c.Id,
                Title = c.Title,
                PosterUrl = c.PosterUrl,
                ItemCount = c.ItemCount,
            })];
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                // The page loaded again; its own loads show it.
                return;
            }
            _logger.LogWarning(ex, "Could not load the server collections of a library");
            _failed.Add(library.LibraryId);
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                _loading.Remove(library.LibraryId);
            }
        }
        if (!token.IsCancellationRequested)
        {
            Refresh();
        }
    }
}
