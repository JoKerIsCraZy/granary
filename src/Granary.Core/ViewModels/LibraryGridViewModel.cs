using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// A poster grid over one catalog listing, loaded by window as Silo's web client loads it (docs/api-contract.md, 7.4).
/// <see cref="StartAsync"/> loads the first page, whose total sizes the grid: it holds a place for every title, and a
/// title not loaded yet is <see cref="MediaCard.Placeholder"/>. The pages around what the view shows
/// (<see cref="ShowViewport"/>) load at their list positions, from the first page's window cursor, so a scroll or a
/// jump far down does not wait for the titles before it. The query carries the image parameters, which every page
/// keeps because cursors are bound to them.
/// </summary>
public sealed partial class LibraryGridViewModel(CatalogApi catalog, ILogger logger) : ObservableObject
{
    /// <summary>Titles per page when the query names none, as the web client asks for.</summary>
    public const int DefaultPageSize = 60;

    // Pages loaded on either side of the ones in view, so a scroll finds its titles there.
    private const int BufferPages = 1;

    // A page that came back short with more to follow is completed from its own cursor, this many times at most.
    private const int MaxFills = 3;

    // Pages load side by side; their ends run on the UI thread in the app, but not under every caller.
    private readonly Lock _gate = new();
    private readonly Dictionary<int, PageLoad> _pages = [];
    private Uri? _server;
    private CatalogQuery? _query;
    private int _pageSize = DefaultPageSize;
    private string _windowCursor = "";
    private bool _totalExact = true;
    private bool _restartedForCursor;
    private CancellationTokenSource _run = new();
    private (int First, int Last) _inView = (0, DefaultPageSize - 1);

    /// <summary>A card or a <see cref="MediaCard.Placeholder"/> for every title; a new listing brings a new list.</summary>
    [ObservableProperty]
    public partial ObservableCollection<MediaCard> Cards { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsReady), nameof(IsEmpty), nameof(HasError), nameof(TotalText))]
    public partial LibraryLoadState State { get; private set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; private set; } = "";

    /// <summary>Titles in the whole listing, as the first page reported.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText))]
    public partial int Total { get; private set; }

    /// <summary>A page in view failed: its places stay placeholders, and <see cref="RetryPagesCommand"/> asks again.</summary>
    [ObservableProperty]
    public partial bool HasPageError { get; private set; }

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsReady => State == LibraryLoadState.Ready;

    public bool IsEmpty => State == LibraryLoadState.Empty;

    public bool HasError => State == LibraryLoadState.Error;

    /// <summary>"1,234 titles" once the first page arrived; "About …" when the server only estimated.</summary>
    public string TotalText => State is LibraryLoadState.Ready or LibraryLoadState.Empty
        ? (_totalExact ? "" : "About ") + Total.ToString("N0", CultureInfo.InvariantCulture) + (Total == 1 ? " title" : " titles")
        : "";

    /// <summary>Raised when a listing starts over, so the view can scroll back to its top.</summary>
    public event EventHandler? Restarted;

    /// <summary>
    /// Raised when the listing changed under its cursor and started over by itself: list positions taken from it before
    /// (a letter index) are stale.
    /// </summary>
    public event EventHandler? ListingChanged;

    /// <summary>Drops the current listing and loads the first page of <paramref name="query"/>, and the next one.</summary>
    public Task StartAsync(Uri server, CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(query);
        _restartedForCursor = false;
        return BeginAsync(server, query);
    }

    /// <summary>Stops the listing and forgets its cards.</summary>
    public void Reset()
    {
        Restart();
        _server = null;
        _query = null;
        State = LibraryLoadState.Idle;
    }

    /// <summary>Loads the pages of the titles in view and those next to them, and stops loading pages scrolled far away.</summary>
    [RelayCommand]
    public void ShowViewport(GridViewport? viewport)
    {
        if (viewport is null)
        {
            return;
        }
        lock (_gate)
        {
            _inView = (Math.Max(0, viewport.First), Math.Max(viewport.First, viewport.Last));
            LoadWindow();
        }
    }

    /// <summary>Loads the first page again after it failed.</summary>
    [RelayCommand]
    private Task RetryAsync() => _server is { } server && _query is { } query ? StartAsync(server, query) : Task.CompletedTask;

    /// <summary>Asks again for the pages that failed.</summary>
    [RelayCommand]
    private void RetryPages()
    {
        lock (_gate)
        {
            foreach (int page in _pages.Where(p => p.Value.HasFailed).Select(p => p.Key).ToList())
            {
                _pages.Remove(page);
            }
            HasPageError = false;
            LoadWindow();
        }
    }

    private async Task BeginAsync(Uri server, CatalogQuery query)
    {
        // The token, not its source: a newer listing disposes the source.
        var token = Restart().Token;
        _server = server;
        _pageSize = query.Limit ?? DefaultPageSize;
        var listing = _query = query with { Limit = _pageSize };
        _inView = (0, _pageSize - 1);
        State = LibraryLoadState.Loading;
        Restarted?.Invoke(this, EventArgs.Empty);
        CatalogBrowseCollection first;
        PageRead read;
        try
        {
            first = await catalog.BrowseAsync(server, listing.WithCursor(null), token);
            read = await CompleteAsync(server, listing, first, token);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (token.IsCancellationRequested)
            {
                // A newer listing replaced this one; its state is not this one's to set.
                return;
            }
            logger.LogWarning(ex, "Could not load the first page of a library grid");
            ErrorMessage = LibraryErrors.Describe(ex);
            State = LibraryLoadState.Error;
            return;
        }
        lock (_gate)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }
            ShowFirstPage(first, read, server);
            LoadWindow();
        }
    }

    private CancellationTokenSource Restart()
    {
        CancellationTokenSource previous;
        CancellationTokenSource fresh = new();
        lock (_gate)
        {
            // Replaced before it is cancelled: continuations that resume inside Cancel already see the new run.
            previous = _run;
            _run = fresh;
            _pages.Clear();
            _windowCursor = "";
            _totalExact = true;
            Cards = [];
            Total = 0;
            ErrorMessage = "";
            HasPageError = false;
        }
        previous.Cancel();
        previous.Dispose();
        return fresh;
    }

    private void ShowFirstPage(CatalogBrowseCollection first, PageRead read, Uri server)
    {
        _windowCursor = first.WindowCursor;
        _totalExact = first.TotalExact;
        int count = read.Items.Count;
        if (read.HasMore)
        {
            // An estimate may fall short: room for one more page until a page says where the listing ends.
            count = Math.Max(first.Total, _pageSize + (first.TotalExact ? 0 : _pageSize));
        }
        var cards = new List<MediaCard>(count);
        cards.AddRange(read.Items.Select(item => MediaCardFactory.Poster(item, server)));
        cards.AddRange(Enumerable.Repeat(MediaCard.Placeholder, Math.Max(0, count - cards.Count)));
        Cards = new ObservableCollection<MediaCard>(cards);
        _pages[0] = PageLoad.Done(read.Continuation);
        Total = Math.Max(first.Total, read.Items.Count);
        State = cards.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        OnPropertyChanged(nameof(TotalText));
    }

    // Called under the gate.
    private void LoadWindow()
    {
        if (State != LibraryLoadState.Ready || _server is null || _query is null || _windowCursor.Length == 0)
        {
            return;
        }
        int lastPage = (Cards.Count - 1) / _pageSize;
        int start = Math.Max(0, _inView.First / _pageSize - BufferPages);
        int end = Math.Min(lastPage, _inView.Last / _pageSize + BufferPages);
        // A page scrolled away is not waited for; one that failed out there is asked again when it comes back.
        foreach (var (page, load) in _pages.Where(p => (p.Key < start || p.Key > end) && !p.Value.IsDone).ToList())
        {
            load.Cancel();
            _pages.Remove(page);
        }
        HasPageError = _pages.Values.Any(p => p.HasFailed);
        var run = _run;
        // A page that came back at once may have started the listing over: the rest of this window is not its.
        for (int page = start; page <= end && ReferenceEquals(run, _run); page++)
        {
            if (!_pages.ContainsKey(page))
            {
                _ = LoadPageAsync(page);
            }
        }
    }

    // Called under the gate, which it takes again for the page's end. Nothing it throws goes unobserved: a page that
    // failed for any reason is marked so, and Retry asks for it again.
    private async Task LoadPageAsync(int page)
    {
        var load = PageLoad.Start(_run.Token);
        _pages[page] = load;
        var server = _server!;
        var listing = _query!;
        // Right after a loaded full page the server continues from its cursor; anywhere else it seeks the position. The
        // first page counted the titles; the others skip it, as the web client's do.
        var query = _pages.TryGetValue(page - 1, out var previous) && previous.NextCursor is { } next
            ? listing.WithCursor(next) with { SkipTotal = true }
            : listing.AtPosition(_windowCursor, page * _pageSize);
        try
        {
            var result = await catalog.BrowseAsync(server, query, load.Token);
            var read = await CompleteAsync(server, listing, result, load.Token);
            lock (_gate)
            {
                if (IsCurrent(page, load))
                {
                    load.Finish(read.Continuation);
                    Place(page, read, server);
                    LoadWindow();
                }
            }
        }
        catch (SiloApiException ex) when (ex.Code == ProblemCodes.InvalidCursor)
        {
            bool startOver;
            lock (_gate)
            {
                startOver = IsCurrent(page, load) && !_restartedForCursor;
                if (!startOver && IsCurrent(page, load))
                {
                    Fail(load, ex);
                }
            }
            if (startOver)
            {
                // The listing changed under its window cursor: start over from page one, once.
                logger.LogInformation("A catalog cursor was refused; the listing starts over");
                _restartedForCursor = true;
                ListingChanged?.Invoke(this, EventArgs.Empty);
                await BeginAsync(server, listing);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                // Scrolled away, or a newer listing replaced this one: nothing to tell.
                if (IsCurrent(page, load))
                {
                    Fail(load, ex);
                }
            }
        }
    }

    private void Fail(PageLoad load, Exception ex)
    {
        if (LibraryErrors.IsExpected(ex))
        {
            logger.LogWarning(ex, "Could not load a page of a library grid");
        }
        else
        {
            logger.LogError(ex, "A page of a library grid failed unexpectedly");
        }
        load.Fail();
        HasPageError = true;
    }

    /// <summary>
    /// A page may come back short with more to follow: its remaining places come from its own cursor, a few times at most.
    /// Titles beyond the page belong to the next one, which loads on its own.
    /// </summary>
    private async Task<PageRead> CompleteAsync(Uri server, CatalogQuery listing, CatalogBrowseCollection result, CancellationToken cancellationToken)
    {
        if (result.Items.Count >= _pageSize || result.NextCursor is null)
        {
            // A full page continues where the next one starts.
            return new PageRead(result.Items, result.NextCursor is not null, result.Items.Count == _pageSize ? result.NextCursor : null);
        }
        var items = new List<CatalogItem>(result.Items);
        string? next = result.NextCursor;
        for (int fill = 0; fill < MaxFills && next is not null && items.Count < _pageSize; fill++)
        {
            var more = await catalog.BrowseAsync(server, listing.WithCursor(next) with { SkipTotal = true }, cancellationToken);
            int room = _pageSize - items.Count;
            items.AddRange(more.Items.Take(room));
            if (more.Items.Count > room)
            {
                return new PageRead(items, HasMore: true, Continuation: null);
            }
            next = more.NextCursor;
        }
        return new PageRead(items, next is not null, items.Count == _pageSize ? next : null);
    }

    private bool IsCurrent(int page, PageLoad load) =>
        !load.IsCancellationRequested && _pages.TryGetValue(page, out var current) && ReferenceEquals(current, load);

    // The page's titles take their places. A page with nothing after it ends the listing there; a full page at the end of
    // an estimate makes room for another one.
    private void Place(int page, PageRead read, Uri server)
    {
        var cards = Cards;
        int start = page * _pageSize;
        for (int i = 0; i < read.Items.Count; i++)
        {
            var card = MediaCardFactory.Poster(read.Items[i], server);
            if (start + i < cards.Count)
            {
                cards[start + i] = card;
            }
            else
            {
                cards.Add(card);
            }
        }
        int end = start + read.Items.Count;
        if (!read.HasMore)
        {
            EndAt(end);
        }
        else if (end >= cards.Count)
        {
            for (int i = 0; i < _pageSize; i++)
            {
                cards.Add(MediaCard.Placeholder);
            }
        }
    }

    // Where the listing ends is known now, whatever the first page counted.
    private void EndAt(int end)
    {
        if (Cards.Count - end > _pageSize)
        {
            // An estimate far off: one new list, not a removal per place.
            Cards = new ObservableCollection<MediaCard>(Cards.Take(end));
        }
        else
        {
            while (Cards.Count > end)
            {
                Cards.RemoveAt(Cards.Count - 1);
            }
        }
        int lastPage = (end - 1) / _pageSize;
        foreach (var (later, load) in _pages.Where(p => p.Key > lastPage).ToList())
        {
            load.Cancel();
            _pages.Remove(later);
        }
        _totalExact = true;
        Total = Cards.Count;
        State = Cards.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        OnPropertyChanged(nameof(TotalText));
    }

    /// <summary>A page's titles, whether more follow, and the cursor the next page continues from (only after a full page).</summary>
    private sealed record PageRead(IReadOnlyList<CatalogItem> Items, bool HasMore, string? Continuation);

    /// <summary>One page's load: on its way, done (with the cursor the next page continues from), or failed.</summary>
    private sealed class PageLoad
    {
        private readonly CancellationTokenSource? _cancel;

        private PageLoad(CancellationTokenSource? cancel) => _cancel = cancel;

        public bool IsDone { get; private set; }

        public bool HasFailed { get; private set; }

        public string? NextCursor { get; private set; }

        public CancellationToken Token => _cancel?.Token ?? CancellationToken.None;

        public bool IsCancellationRequested => _cancel?.IsCancellationRequested ?? false;

        public static PageLoad Start(CancellationToken run) => new(CancellationTokenSource.CreateLinkedTokenSource(run));

        public static PageLoad Done(string? nextCursor) => new(null) { IsDone = true, NextCursor = nextCursor };

        public void Finish(string? nextCursor)
        {
            IsDone = true;
            NextCursor = nextCursor;
            _cancel?.Dispose();
        }

        public void Fail()
        {
            HasFailed = true;
            _cancel?.Dispose();
        }

        public void Cancel()
        {
            if (!IsDone && !HasFailed)
            {
                _cancel?.Cancel();
                _cancel?.Dispose();
            }
        }
    }
}
