using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Servers;
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
/// Search as on Silo's web client (docs/api-contract.md, 7.5): titles 300 ms after the last keystroke,
/// in relevance order and cursor-paged, and the people whose names match in parallel when the server
/// can search them, and under them the titles TMDB finds that the library does not have (<see cref="Requests"/>).
/// A person opens their page, as the web's search does.
/// A new search cancels the previous one, and an answer that arrives for an older text is dropped.
/// </summary>
/// <remarks>The commands take no cancellation token: a cancellable toolkit command cancels its running call when executed again.</remarks>
public sealed partial class SearchViewModel : ObservableObject
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    public const int PageSize = 60;
    public const int PeopleLimit = 20;

    internal const string BusyText = "Search is busy, try again in a moment";

    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<SearchViewModel> _logger;

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
    // What the backdrop shows of each library result, by the card's id.
    private readonly Dictionary<string, BrowseHeroTitle> _backdropTitles = new(StringComparer.Ordinal);
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
        ILogger<SearchViewModel> logger)
    {
        _session = session;
        _catalog = catalog;
        _images = images;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        Backdrop = new BrowseHeroViewModel(dispatcher, time);
        Requests = requests;
        Requests.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RequestToAddViewModel.IsShown) or nameof(RequestToAddViewModel.IsSearching))
            {
                OnPropertyChanged(nameof(ShowsRequests));
                ShowResultsState();
            }
        };
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

    /// <summary>The titles of the search.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MediaCard> Cards { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPeople), nameof(ShowsTitlesHeading))]
    public partial IReadOnlyList<SearchPerson> People { get; private set; } = [];

    /// <summary>
    /// The library found nothing, and "Request to add" is searching or shows titles: it says so instead of "No titles
    /// found", as the web hides its empty grid then.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTitlesHeading))]
    public partial bool IsLibraryRescued { get; private set; }

    /// <summary>Titles or people are being searched.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    /// <summary>The last request failed; the page offers <see cref="RetryCommand"/>.</summary>
    [ObservableProperty]
    public partial bool HasError { get; private set; }

    /// <summary>"No titles found", the error, or what is loading; also for screen readers.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    /// <summary>"Search", or "Results for "heat"".</summary>
    public string Heading => ResultsQuery.Length > 0 ? $"Results for \"{ResultsQuery}\"" : "Search";

    public bool ShowsPeople => People.Count > 0;

    /// <summary>"Titles" between the people and the library's titles, unless "Request to add" stands in for none.</summary>
    public bool ShowsTitlesHeading => ShowsPeople && !IsLibraryRescued;

    public bool ShowsRequests => Requests.IsShown;

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

    /// <summary>Opens the person's page; the results stay for Back.</summary>
    [RelayCommand]
    private void OpenPerson(SearchPerson? person)
    {
        if (person is not null)
        {
            _navigation.NavigateTo(AppRoute.Person, person.Id);
        }
    }

    /// <summary>Searches the box's text after <paramref name="delay"/>, cancelling whatever ran for an older text.</summary>
    private void Schedule(TimeSpan delay)
    {
        _searchRun?.Cancel();
        SetSearchingTitles(false);
        string text = Query.Trim();
        _scheduledText = text;
        if (text.Length == 0)
        {
            _searchRun = null;
            ShowNoSearch();
            Searching = Task.CompletedTask;
            return;
        }
        var run = new CancellationTokenSource();
        _searchRun = run;
        Searching = SearchAsync(text, Scope, delay, run);
    }

    private async Task SearchAsync(string text, SearchScope scope, TimeSpan delay, CancellationTokenSource run)
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
            _backdropTitles.Clear();
            SetSearchingTitles(true);
            var people = FindPeopleAsync(server, text, cancellationToken);
            var requests = Requests.SearchAsync(text, scope);
            BrowseCardPager? titles = null;
            Exception? failure = null;
            try
            {
                var images = await BrowseArtwork.ParametersAsync(_images, server, DisplayScale, _logger, cancellationToken);
                var query = new CatalogQuery { Q = text, Type = TypeOf(scope), Limit = PageSize, Images = images };
                titles = new BrowseCardPager(_catalog, server.BaseUri, query, item => CardFor(item, server.BaseUri));
                await titles.LoadFirstPageAsync(cancellationToken);
            }
            catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) && !cancellationToken.IsCancellationRequested)
            {
                failure = ex;
            }
            var found = await people;
            cancellationToken.ThrowIfCancellationRequested();
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
        if (failure is not null || titles is null)
        {
            _logger.LogWarning("Searching titles failed: {Failure}", failure is null ? "no answer" : ApiFailureText.Describe(failure));
            _resultsState = (failure is null ? "Search failed. Try again." : Describe(failure), true);
        }
        else
        {
            _resultsState = (titles.Cards.Count == 0 ? $"No titles found for \"{text}\"" : "", false);
        }
        _libraryEmpty = failure is null && titles is { Cards.Count: 0 };
        Requests.LibraryFoundNothing = _libraryEmpty;
        _isSearchingTitles = false;
        Cards = titles is null ? [] : titles.Cards;
        ForgetBackdrop();
        ShowResultsState();
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

    // New results: no backdrop until one of them is pointed at, and that one shows at once.
    private void ForgetBackdrop()
    {
        Backdrop.Reset(null);
        Backdrop.Forget();
    }

    /// <summary>
    /// The results' busy ring, word and error. While the library found nothing, "Request to add" may still find titles:
    /// the ring turns until it knows, and "No titles found" waits until it found none.
    /// </summary>
    private void ShowResultsState()
    {
        IsLibraryRescued = _libraryEmpty && (Requests.IsSearching || Requests.IsShown);
        IsSearching = _isSearchingTitles || (_libraryEmpty && Requests.IsSearching);
        (StatusText, HasError) = IsLibraryRescued ? ("", false) : _resultsState;
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
        Cards = [];
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
        ShowNoSearch();
        if (Query.Trim().Length > 0)
        {
            Schedule(TimeSpan.Zero);
        }
    }

    // The search deadline (3 s) answers 503 with Retry-After.
    private static string Describe(Exception ex) => ex is SiloApiException { Status: 503 }
        ? BusyText
        : BrowseFailureText.Describe(ex, "Search failed. Try again.");

    private static string TypeOf(SearchScope scope) => scope switch
    {
        SearchScope.Movies => MediaScopes.Movie,
        SearchScope.Series => MediaScopes.Series,
        _ => MediaScopes.Video,
    };

    // An item without an id could not be opened. What the backdrop shows of it is kept with it.
    private MediaCard? CardFor(CatalogItem item, Uri server)
    {
        if (item.ContentId.Length == 0)
        {
            return null;
        }
        _backdropTitles[item.ContentId] = BrowseHeroTitle.From(item, server);
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
        }
    }
}
