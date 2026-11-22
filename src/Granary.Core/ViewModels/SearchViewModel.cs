using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Servers;
using Granary.Core.Settings;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>What Search looks for: <c>type=video</c>, <c>movie</c> or <c>series</c>.</summary>
public enum SearchScope
{
    MoviesAndSeries,
    Movies,
    Series,
}

/// <summary>
/// Search as on Silo's web client (docs/api-contract.md, 7.5; the web's pages/SearchResults.tsx): titles 300 ms after the last
/// keystroke, in relevance order unless another sort is picked, cursor-paged, and the people whose names match in parallel
/// when the server can search them, and the titles TMDB finds that the library does not have (<see cref="Requests"/>). The
/// overview shows two rows of the library's titles, the titles to request, people and three more rows; the whole library
/// list and the whole request list are views of their own (<see cref="View"/>). The box sets the text finds lead, as on the
/// web (<see cref="BoxSets"/>). A person opens their page, as the web's search does. A new search cancels the previous one,
/// and an answer that arrives for an older text is dropped.
/// </summary>
/// <remarks>The commands take no cancellation token: a cancellable toolkit command cancels its running call when executed again.</remarks>
public sealed partial class SearchViewModel : ObservableObject
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    public const int PageSize = 60;
    public const int PeopleLimit = 20;

    internal static string BusyText => AppText.Get("granary.search.busy");

    /// <summary>How long the titles wait for the box sets, as Android's search: box sets only add to them.</summary>
    public static readonly TimeSpan BoxSetGrace = TimeSpan.FromMilliseconds(1500);

    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<SearchViewModel> _logger;
    private readonly BoxSetsApi? _boxSets;

    // One run per search text (its wait, titles and people).
    private CancellationTokenSource? _searchRun;
    private string? _scheduledText;
    private BrowseCardPager? _titles;
    private (string Text, IReadOnlyList<SearchPerson> People)? _peopleFound;
    private (string Status, bool HasError) _resultsState = ("", false);
    private Task<CatalogSearchCapabilities?>? _capabilities;
    private bool _isSearchingTitles;
    // The last search found no titles, without failing.
    private bool _libraryEmpty;
    // What the backdrop shows of each library result, by the card's id. Each search has its own, so one a newer search
    // replaced adds nothing to it.
    private Dictionary<string, BrowseHeroTitle> _backdropTitles = new(StringComparer.Ordinal);
    // The page each box set card opens, by the card's id.
    private Dictionary<string, BoxSetRouteParameter> _boxSetPages = new(StringComparer.Ordinal);
    private (bool IsReady, string? ServerId, string? ProfileId)? _sessionKey;
    private bool _isActive;

    public SearchViewModel(
        ISessionManager session,
        CatalogApi catalog,
        ImageParameterCache images,
        INavigationService navigation,
        IUiDispatcher dispatcher,
        TimeProvider time,
        RequestToAddViewModel requests,
        ILogger<SearchViewModel> logger,
        BoxSetsApi? boxSets = null)
    {
        _session = session;
        _catalog = catalog;
        _images = images;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _boxSets = boxSets;
        Backdrop = new BrowseHeroViewModel(dispatcher, time);
        Requests = requests;
        Requests.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RequestToAddViewModel.IsShown) or nameof(RequestToAddViewModel.IsSearching)
                or nameof(RequestToAddViewModel.CanLoadMore))
            {
                OnPropertyChanged(nameof(ShowsRequests));
                ShowResultsState();
            }
        };
        Requests.Cards.CollectionChanged += (_, _) => ShowRequestRow();
        Filters = new LibraryFiltersViewModel(SearchAgainAsync, logger);
    }

    /// <summary>"Request to add": what TMDB finds for the text that the library does not have.</summary>
    public RequestToAddViewModel Requests { get; }

    /// <summary>
    /// The dimmed backdrop behind the header: the result the pointer or the keyboard is on, in the titles or in "Request to
    /// add"; nothing until one is pointed at, and nothing again for each new search.
    /// </summary>
    public BrowseHeroViewModel Backdrop { get; }

    /// <summary>The search box; every change searches again once typing pauses.</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial SearchScope Scope { get; set; }

    /// <summary>The text the shown results are for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    public partial string ResultsQuery { get; private set; } = "";

    /// <summary>The box sets the text finds, best match first, as cards ahead of people and titles (the web's search).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBoxSets))]
    public partial IReadOnlyList<MediaCard> BoxSets { get; private set; } = [];

    /// <summary>The box sets lead the overview only, as on the web.</summary>
    public bool ShowsBoxSets => BoxSets.Count > 0 && ShownView == SearchView.Overview;

    /// <summary>Every title of the search, for the library view; it grows as that view scrolls.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MediaCard> Cards { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPeople), nameof(ShowsPeopleSection), nameof(ShowsPeopleAfterRequests), nameof(ShowsPeopleAfterMore))]
    public partial IReadOnlyList<SearchPerson> People { get; private set; } = [];

    /// <summary>
    /// The library found nothing, and "Request to add" is searching or shows titles: it says so instead of "No items
    /// found.", as the web hides its empty grid then.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLibraryRescued { get; private set; }

    /// <summary>Titles or people are being searched.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    /// <summary>The last request failed; the page offers <see cref="RetryCommand"/>.</summary>
    [ObservableProperty]
    public partial bool HasError { get; private set; }

    /// <summary>"No items found.", the error, or what is loading; also for screen readers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsStatus))]
    public partial string StatusText { get; private set; } = "";

    /// <summary>"Search", or "Results for "heat"": the page's name for screen readers, as the web's hidden heading.</summary>
    public string Heading => ResultsQuery.Length > 0
        ? AppText.Get("web.catalog.page.resultsFor", ("query", ResultsQuery))
        : AppText.Get("web.catalog.page.searchTitle");

    /// <summary>People show in the overview only, as on the web.</summary>
    public bool ShowsPeople => People.Count > 0 && ShownView == SearchView.Overview;

    /// <summary>The people lookup failed: "Could not load people results." with Retry people search, as the web's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPeopleSection), nameof(ShowsPeopleAfterRequests), nameof(ShowsPeopleAfterMore))]
    public partial bool HasPeopleError { get; private set; }

    /// <summary>The People section: people found, or the lookup's failure; in the overview only.</summary>
    public bool ShowsPeopleSection => (People.Count > 0 || HasPeopleError) && ShownView == SearchView.Overview;

    /// <summary>Asks for the people of the shown text again, after a failure.</summary>
    [RelayCommand]
    private async Task RetryPeopleAsync()
    {
        string text = ResultsQuery;
        if (text.Length == 0 || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        var cancellationToken = _searchRun?.Token ?? CancellationToken.None;
        IReadOnlyList<SearchPerson>? people;
        try
        {
            people = await FindPeopleAsync(server, text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A new text replaced the search meanwhile: its own run shows the people.
            return;
        }
        if (text != ResultsQuery)
        {
            return;
        }
        _peopleFound = people is null ? null : (text, people);
        People = people ?? [];
        HasPeopleError = people is null;
        ShowResultsState();
    }

    /// <summary>TMDB found titles to add, or has more pages to look through: "Request to add" has a view.</summary>
    public bool ShowsRequests => Requests.IsShown;

    /// <summary>The type menu's name for the type searched, as the web's media type menu.</summary>
    public string ScopeLabel => AppText.Get(Scope switch
    {
        SearchScope.Movies => "web.catalog.mediaType.movies",
        SearchScope.Series => "web.catalog.mediaType.series",
        _ => "web.catalog.mediaType.moviesAndSeries",
    });

    /// <summary>The display's scale (1.5 at 150 %), so artwork is asked for at the pixels it is drawn at.</summary>
    public double DisplayScale { get; set; } = 1;

    /// <summary>The current search, for tests.</summary>
    internal Task Searching { get; private set; } = Task.CompletedTask;

    /// <summary>The page is shown; <paramref name="query"/> (the route's parameter) searches at once.</summary>
    public Task ActivateAsync(string? query = null)
    {
        if (!_isActive)
        {
            _isActive = true;
            _session.Changed += OnSessionChanged;
            Requests.RequestNotice.Attach();
        }
        if (_sessionKey != SessionKey())
        {
            StartOver();
        }
        PrepareSortsAndFilters();
        string text = query?.Trim() ?? "";
        if (text.Length > 0 && (text != Query.Trim() || text != ResultsQuery))
        {
            Query = text;
            Schedule(TimeSpan.Zero);
        }
        return Searching;
    }

    /// <summary>The page left; the shown results stay for a return.</summary>
    public void Deactivate()
    {
        if (_isActive)
        {
            _isActive = false;
            _session.Changed -= OnSessionChanged;
            Requests.RequestNotice.Detach();
        }
    }

    partial void OnQueryChanged(string value)
    {
        // A space typed at the end does not change what is searched.
        if (_searchRun is not null && value.Trim() == _scheduledText)
        {
            return;
        }
        Schedule(DebounceDelay);
    }

    partial void OnScopeChanged(SearchScope value)
    {
        OnPropertyChanged(nameof(ScopeLabel));
        OfferSorts();
        PrepareSortsAndFilters();
        if (Query.Trim().Length > 0)
        {
            Schedule(TimeSpan.Zero);
        }
    }

    /// <summary>Enter in the search box: no waiting for the pause.</summary>
    [RelayCommand]
    private Task SearchNowAsync()
    {
        Schedule(TimeSpan.Zero);
        return Searching;
    }

    [RelayCommand]
    private void Clear() => Query = "";

    /// <summary>For the grid while it scrolls; after a failure only <see cref="RetryCommand"/> asks again.</summary>
    [RelayCommand]
    private Task LoadMoreAsync() => HasError ? Task.CompletedTask : LoadNextPageAsync();

    [RelayCommand]
    private Task RetryAsync()
    {
        if (_titles is { IsLoaded: true } && ResultsQuery == Query.Trim())
        {
            return LoadNextPageAsync();
        }
        return SearchNowAsync();
    }

    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null)
        {
            _navigation.NavigateTo(AppRoute.Item, card.Id);
        }
    }

    /// <summary>Opens a box set's page; the results stay for Back.</summary>
    [RelayCommand]
    private void OpenBoxSet(MediaCard? card)
    {
        if (card is not null && _boxSetPages.TryGetValue(card.Id, out var page))
        {
            _navigation.NavigateTo(AppRoute.BoxSet, page);
        }
    }

    /// <summary>Opens the person's page; the results stay for Back.</summary>
    [RelayCommand]
    private void OpenPerson(SearchPerson? person)
    {
        if (person is not null)
        {
            _navigation.NavigateTo(AppRoute.Person, person.Id);
        }
    }

    /// <summary>
    /// Searches the box's text after <paramref name="delay"/>, cancelling whatever ran for an older text. Another sort or
    /// filters ask the library again, but TMDB knows neither: its titles to request stay (<paramref name="keepRequests"/>).
    /// </summary>
    private void Schedule(TimeSpan delay, bool keepRequests = false)
    {
        _searchRun?.Cancel();
        SetSearchingTitles(false);
        string text = Query.Trim();
        _scheduledText = text;
        // A new text starts at the overview, as the web's new search has no view; a sort, filters or a scope keep the view.
        if (text != ResultsQuery)
        {
            View = SearchView.Overview;
        }
        if (text.Length == 0)
        {
            _searchRun = null;
            ShowNoSearch();
            Searching = Task.CompletedTask;
            return;
        }
        var run = new CancellationTokenSource();
        _searchRun = run;
        Searching = SearchAsync(text, Scope, delay, run, keepRequests && text == ResultsQuery && text == Requests.Query);
    }

    private async Task SearchAsync(string text, SearchScope scope, TimeSpan delay, CancellationTokenSource run, bool keepRequests)
    {
        var cancellationToken = run.Token;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, cancellationToken);
            }
            if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
            {
                return;
            }
            // What the last text found is no guide to this one's "Request to add", nor to its backdrop.
            _libraryEmpty = false;
            var backdrops = new Dictionary<string, BrowseHeroTitle>(StringComparer.Ordinal);
            _backdropTitles = backdrops;
            SetSearchingTitles(true);
            var people = FindPeopleAsync(server, text, cancellationToken);
            var boxSets = FindBoxSetsAsync(server, text, scope, cancellationToken);
            var requests = keepRequests ? Task.CompletedTask : Requests.SearchAsync(text, scope);
            BrowseCardPager? titles = null;
            Exception? failure = null;
            try
            {
                var images = await BrowseArtwork.ParametersAsync(_images, server, DisplayScale, _logger, cancellationToken);
                // No sort under Relevance: the server ranks a text by it, never by the library's Date Added default.
                var query = Filters.ApplyTo(new CatalogQuery
                {
                    Q = text,
                    Type = TypeOf(scope),
                    Sort = Sort is { } sort ? new CatalogSort(sort.Field, IsDescending) : null,
                    Limit = PageSize,
                    Images = images,
                });
                titles = new BrowseCardPager(_catalog, server.BaseUri, query, item => CardFor(item, server.BaseUri, backdrops));
                await titles.LoadFirstPageAsync(cancellationToken);
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) && !cancellationToken.IsCancellationRequested)
            {
                failure = ex;
            }
            var found = await people;
            var foundBoxSets = await boxSets;
            cancellationToken.ThrowIfCancellationRequested();
            ShowBoxSets(server.BaseUri, foundBoxSets);
            ShowResults(text, titles, found, failure);
            await requests;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ApiFailureText.IsRequestFailure(ex))
        {
            // A newer text replaced this search; its answer, or its failure, is stale.
        }
        finally
        {
            if (run == _searchRun)
            {
                SetSearchingTitles(false);
            }
        }
    }

    /// <summary>
    /// People whose names match; none when the server cannot search them, and null when the lookup or the
    /// capabilities read failed (shown as none, but not remembered).
    /// </summary>
    private async Task<IReadOnlyList<SearchPerson>?> FindPeopleAsync(ServerEntry server, string text, CancellationToken cancellationToken)
    {
        // A type switch finds the same people.
        if (_peopleFound is { } found && found.Text == text)
        {
            return found.People;
        }
        var capabilities = await GetCapabilitiesAsync(server, cancellationToken);
        if (capabilities is null)
        {
            return null;
        }
        if (!capabilities.PeopleMediaScope)
        {
            return [];
        }
        try
        {
            var people = await _catalog.SearchPeopleAsync(server.BaseUri, text, PeopleLimit, MediaScopes.Video, cancellationToken);
            return [.. people.Items.Where(person => person.Id.Length > 0).Select(person => SearchPerson.From(person, server.BaseUri))];
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            _logger.LogWarning("Searching people failed: {Failure}", ApiFailureText.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// The box sets the text finds (docs/api-contract.md, 7.18): only while the server has them (<c>box_sets</c> in the
    /// search capabilities, as the Apple app asks), and not for Series, as box sets are films. A failed read finds none
    /// rather than failing the titles, and one slower than <see cref="BoxSetGrace"/> does not hold them up, as Android's.
    /// </summary>
    private async Task<IReadOnlyList<CatalogBoxSet>> FindBoxSetsAsync(ServerEntry server, string text, SearchScope scope, CancellationToken cancellationToken)
    {
        if (_boxSets is null || scope == SearchScope.Series)
        {
            return [];
        }
        // The grace starts with the search, before anything is awaited, and ends the read with it.
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var grace = Task.Delay(BoxSetGrace, _time, run.Token);
        var read = ReadBoxSetsAsync(_boxSets, server, text, run.Token);
        var first = await Task.WhenAny(read, grace);
        await run.CancelAsync();
        if (first == read)
        {
            return await read;
        }
        _ = ObserveAsync(read);
        return [];
    }

    // A read given up on still ends: what it ends with is logged, so no failure goes unobserved.
    private async Task ObserveAsync(Task<IReadOnlyList<CatalogBoxSet>> read)
    {
        try
        {
            await read;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("A box set search given up on failed: {Failure}", ApiFailureText.Describe(ex));
        }
    }

    private async Task<IReadOnlyList<CatalogBoxSet>> ReadBoxSetsAsync(BoxSetsApi boxSets, ServerEntry server, string text, CancellationToken cancellationToken)
    {
        try
        {
            var capabilities = await GetCapabilitiesAsync(server, cancellationToken);
            return capabilities is { BoxSets: true } ? await boxSets.SearchAsync(server.BaseUri, text, cancellationToken: cancellationToken) : [];
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Searching box sets failed: {Failure}", ApiFailureText.Describe(ex));
            }
            return [];
        }
    }

    // A box set without an id or library could not be opened.
    private void ShowBoxSets(Uri? server, IReadOnlyList<CatalogBoxSet> boxSets)
    {
        var pages = new Dictionary<string, BoxSetRouteParameter>(StringComparer.Ordinal);
        var cards = new List<MediaCard>();
        foreach (var boxSet in boxSets)
        {
            if (server is null || boxSet.Id.Length == 0 || boxSet.LibraryId.Length == 0)
            {
                continue;
            }
            var card = MediaCardFactory.BoxSet(boxSet, server);
            if (pages.TryAdd(card.Id, new BoxSetRouteParameter(boxSet.Id, boxSet.LibraryId, boxSet.Title)))
            {
                cards.Add(card);
            }
        }
        _boxSetPages = pages;
        BoxSets = cards;
    }

    /// <summary>Read once per session; a failed read is tried again with the next search.</summary>
    private async Task<CatalogSearchCapabilities?> GetCapabilitiesAsync(ServerEntry server, CancellationToken cancellationToken)
    {
        var read = _capabilities ??= ReadCapabilitiesAsync(server);
        var capabilities = await read.WaitAsync(cancellationToken);
        if (capabilities is null && _capabilities == read)
        {
            _capabilities = null;
        }
        return capabilities;
    }

    private async Task<CatalogSearchCapabilities?> ReadCapabilitiesAsync(ServerEntry server)
    {
        try
        {
            return await _catalog.GetSearchCapabilitiesAsync(server.BaseUri);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Reading the search capabilities failed: {Failure}", ApiFailureText.Describe(ex));
            return null;
        }
    }

    private void ShowResults(string text, BrowseCardPager? titles, IReadOnlyList<SearchPerson>? people, Exception? failure)
    {
        _titles = titles;
        _peopleFound = people is null ? null : (text, people);
        ResultsQuery = text;
        People = people ?? [];
        // Null is a lookup that failed; a server that cannot search people found none, and that is no failure.
        HasPeopleError = people is null;
        if (failure is not null || titles is null)
        {
            _logger.LogWarning("Searching titles failed: {Failure}", failure is null ? "no answer" : ApiFailureText.Describe(failure));
            _resultsState = (failure is null ? AppText.Get("web.catalog.page.searchFailed") : Describe(failure), true);
        }
        else
        {
            // The web's words for its empty results grid (components/ItemGrid.tsx).
            _resultsState = (titles.Cards.Count == 0 ? AppText.Get("web.catalog.grid.noItems") : "", false);
        }
        _libraryEmpty = failure is null && titles is { Cards.Count: 0 };
        Requests.LibraryFoundNothing = _libraryEmpty;
        _isSearchingTitles = false;
        Cards = titles is null ? [] : titles.Cards;
        // The overview cuts the first page; the library view reading on does not grow it.
        _firstPage = titles is null ? ([], false) : ([.. titles.Cards], titles.HasMore);
        ForgetBackdrop();
        ShowResultsState();
        ShowOverview();
    }

    /// <summary>The pointer or the keyboard came to a result (a title, or one to request): the backdrop follows it.</summary>
    [RelayCommand]
    private void PointCard(MediaCard? card)
    {
        if (card is not null && (_backdropTitles.GetValueOrDefault(card.Id) ?? Requests.BackdropOf(card)) is { } title)
        {
            Backdrop.Point(title);
        }
    }

    /// <summary>No backdrop until a result is pointed at, and that one shows at once: new results, or a visit that is not Back.</summary>
    public void ForgetBackdrop()
    {
        Backdrop.Reset(null);
        Backdrop.Forget();
    }

    /// <summary>
    /// The results' busy ring, word and error. While the library found nothing, "Request to add" may still find titles:
    /// the ring turns until it knows, and "No items found." waits until it found none.
    /// </summary>
    private void ShowResultsState()
    {
        IsLibraryRescued = _libraryEmpty && (Requests.IsSearching || Requests.IsShown);
        IsSearching = _isSearchingTitles || (_libraryEmpty && Requests.IsSearching);
        // "No items found." only when nothing at all was found, as the web's: people or box sets are an answer too.
        bool othersFound = _libraryEmpty && (People.Count > 0 || HasPeopleError || BoxSets.Count > 0);
        (StatusText, HasError) = IsLibraryRescued || othersFound ? ("", false) : _resultsState;
        ShowViews();
    }

    private void SetSearchingTitles(bool isSearching)
    {
        _isSearchingTitles = isSearching;
        ShowResultsState();
    }

    private async Task LoadNextPageAsync()
    {
        var listing = _titles;
        var run = _searchRun;
        if (listing is not { HasMore: true } || run is null || IsSearching)
        {
            return;
        }
        bool IsShown() => listing == _titles;
        try
        {
            await listing.LoadNextPageAsync(run.Token);
            if (IsShown())
            {
                HasError = false;
                StatusText = "";
            }
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            if (!run.IsCancellationRequested && IsShown())
            {
                _logger.LogWarning("Loading more search results failed: {Failure}", ApiFailureText.Describe(ex));
                HasError = true;
                StatusText = Describe(ex);
            }
        }
        if (IsShown())
        {
            _resultsState = (StatusText, HasError);
            // The error card and its Retry follow a next page's failure and its retry, as a first page's do.
            ShowViews();
        }
    }

    private void ShowNoSearch()
    {
        _titles = null;
        _isSearchingTitles = false;
        _libraryEmpty = false;
        Requests.Clear();
        _resultsState = ("", false);
        ResultsQuery = "";
        People = [];
        HasPeopleError = false;
        ShowBoxSets(null, []);
        Cards = [];
        _firstPage = ([], false);
        ShowOverview();
        ForgetBackdrop();
        IsSearching = false;
        HasError = false;
        StatusText = "";
    }

    /// <summary>Another server or profile: nothing shown or remembered belongs to it.</summary>
    private void StartOver()
    {
        _sessionKey = SessionKey();
        _searchRun?.Cancel();
        _searchRun = null;
        _capabilities = null;
        _peopleFound = null;
        ForgetSortsAndFilters();
        ShowNoSearch();
        if (Query.Trim().Length > 0)
        {
            Schedule(TimeSpan.Zero);
        }
    }

    // The search deadline (3 s) answers 503 with Retry-After.
    private static string Describe(Exception ex) => ex is SiloApiException { Status: 503 }
        ? BusyText
        : BrowseFailureText.Describe(ex, AppText.Get("web.catalog.page.searchFailed"));

    private static string TypeOf(SearchScope scope) => scope switch
    {
        SearchScope.Movies => MediaScopes.Movie,
        SearchScope.Series => MediaScopes.Series,
        _ => MediaScopes.Video,
    };

    // An item without an id could not be opened. What the backdrop shows of it is kept with its search's. Search asks for movies
    // and series only (SearchScope, never video_with_episodes), so the backdrop has no episode to hide.
    private static MediaCard? CardFor(CatalogItem item, Uri server, Dictionary<string, BrowseHeroTitle> backdrops)
    {
        if (item.ContentId.Length == 0)
        {
            return null;
        }
        backdrops[item.ContentId] = BrowseHeroTitle.From(item, server, EpisodeSpoilers.Off);
        return MediaCardFactory.Poster(item, server);
    }

    private (bool IsReady, string? ServerId, string? ProfileId) SessionKey() =>
        (_session.State == SessionState.Ready, _session.ActiveServer?.Id, _session.ProfileId);

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.HasThreadAccess)
        {
            StartOverForNewSession();
        }
        else
        {
            _dispatcher.Post(StartOverForNewSession);
        }
    }

    private void StartOverForNewSession()
    {
        if (_isActive && _sessionKey != SessionKey())
        {
            StartOver();
            // The new session's rating sorts and filter options, as an activation asks for them.
            PrepareSortsAndFilters();
        }
    }
}
