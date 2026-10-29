using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Home: the server's rows as Silo's web client shows them (docs/api-contract.md, 7.1). Rows that came
/// back empty but count cards are fetched again, at most four at a time as Android does; rows without
/// cards are hidden, and Continue Watching keeps only video cards. The page calls
/// <see cref="ActivateAsync"/> when it is shown and <see cref="Deactivate"/> when it leaves: another
/// server or profile reloads at once, a return reloads only rows older than <see cref="StaleAfter"/>, or rows from
/// before a change of the viewer's state (<see cref="ViewerStateChanges"/>: playback, a watched mark, …). Such a
/// change while Home is shown reloads it at once, quietly, the rows keeping where they were scrolled to.
/// </summary>
/// <remarks>
/// The commands take no cancellation token on purpose: a cancellable toolkit command cancels its running
/// call when it is executed again. Loads are cancelled and their stale answers dropped through runs instead.
/// </remarks>
public sealed partial class HomeViewModel : ObservableObject
{
    /// <summary>How long shown rows count as current when the user comes back to Home.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    internal const int MaxConcurrentRowRequests = 4;
    internal const string LoadingText = "Loading Home";
    internal const string EmptyText = "Nothing here yet";

    private readonly ISessionManager _session;
    private readonly CatalogApi _catalog;
    private readonly ImageParameterCache _images;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly ViewerStateChanges? _changes;

    private CancellationTokenSource? _run;
    private (bool IsReady, string? ServerId, string? ProfileId)? _shownFor;
    private DateTimeOffset? _loadedAt;
    private long _loadedVersion;
    private bool _isActive;
    private bool _isReportingOwnChange;

    public HomeViewModel(
        ISessionManager session,
        CatalogApi catalog,
        ImageParameterCache images,
        INavigationService navigation,
        IUiDispatcher dispatcher,
        TimeProvider time,
        ILogger<HomeViewModel> logger,
        ViewerStateChanges? changes = null)
    {
        _session = session;
        _catalog = catalog;
        _images = images;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _changes = changes;
        Hero = new HomeHeroViewModel(dispatcher, time);
    }

    [ObservableProperty]
    public partial IReadOnlyList<HomeRowViewModel> Rows { get; private set; } = [];

    /// <summary>The title at the top of Home, following the card the pointer or the keyboard is on.</summary>
    public HomeHeroViewModel Hero { get; }

    /// <summary>The first answer is on its way and there is nothing to show yet.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Home could not be loaded; the page offers <see cref="RetryCommand"/>.</summary>
    [ObservableProperty]
    public partial bool HasError { get; private set; }

    [ObservableProperty]
    public partial string ErrorText { get; private set; } = "";

    /// <summary>The server answered without a single row with cards.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>What a screen reader hears about the page's state: loading, the error, or empty.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    /// <summary>A short message about an action on a card, such as a removal that failed.</summary>
    [ObservableProperty]
    public partial string Notice { get; private set; } = "";

    /// <summary>The display's scale (1.5 at 150 %), so artwork is asked for at the pixels it is drawn at.</summary>
    public double DisplayScale { get; set; } = 1;

    /// <summary>The current load, for tests.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The page is shown: follow the session and load what is missing or stale.</summary>
    public Task ActivateAsync()
    {
        if (!_isActive)
        {
            _isActive = true;
            _session.Changed += OnSessionChanged;
            _changes?.Changed += OnViewerStateChanged;
        }
        return RefreshIfNeededAsync();
    }

    /// <summary>The page left; a load in flight still completes, so a return finds its rows.</summary>
    public void Deactivate()
    {
        if (_isActive)
        {
            _isActive = false;
            _session.Changed -= OnSessionChanged;
            _changes?.Changed -= OnViewerStateChanged;
        }
    }

    /// <summary>Whether the card has a remove action ("Remove from Continue Watching").</summary>
    public bool CanRemove(MediaCard? card) => card is not null && FindRemovable(card) is { } item && HomeDismissal.For(item) is not null;

    [RelayCommand]
    private Task RetryAsync() => Loading = LoadAsync(quietly: false);

    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null)
        {
            _navigation.NavigateTo(AppRoute.Item, card.Id);
        }
    }

    /// <summary>The pointer or the keyboard came to a card: the top of Home follows it.</summary>
    [RelayCommand]
    private void PointCard(MediaCard? card)
    {
        if (card is not null && _session.ActiveServer is { } server && Rows.Select(r => r.ItemOf(card)).FirstOrDefault(i => i is not null) is { } item)
        {
            Hero.Point(HomeHeroTitle.From(item, server.BaseUri));
        }
    }

    // The rows changed: the top of Home keeps its title while a row still has it, else shows the first card.
    private void FollowRowsWithHero()
    {
        if (_session.ActiveServer is not { } server)
        {
            Hero.Reset(null);
            return;
        }
        var items = Rows.SelectMany(row => row.Cards.Select(row.ItemOf)).OfType<CatalogItem>();
        var item = (Hero.Shown is { } shown ? items.FirstOrDefault(i => i.ContentId == shown.Key) : null) ?? items.FirstOrDefault();
        Hero.Reset(item is null ? null : HomeHeroTitle.From(item, server.BaseUri));
    }

    partial void OnRowsChanged(IReadOnlyList<HomeRowViewModel> value) => FollowRowsWithHero();

    /// <summary>
    /// Removes the card's title from Continue Watching and Next Up. The server drops the whole show from
    /// both rows, so every card of that series goes locally too.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync(MediaCard? card)
    {
        if (card is null || FindRemovable(card) is not { } item || HomeDismissal.For(item) is not { } dismissal
            || _session.ActiveServer is not { } server)
        {
            return;
        }
        var shownFor = _shownFor;
        Notice = "";
        try
        {
            await _catalog.DismissHomeItemAsync(server.BaseUri, dismissal);
            ReportOwnChange();
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Removing a card from Home failed: {Failure}", ApiFailureText.Describe(ex));
            Notice = $"{card.Title} could not be removed. Try again.";
            return;
        }
        if (_shownFor != shownFor)
        {
            // Another server or profile took over meanwhile: its rows are not this card's.
            return;
        }
        string? series = item.Type == CatalogItemTypes.Series ? item.ContentId : item.SeriesId;
        bool SameTitle(CatalogItem other) =>
            other.ContentId == item.ContentId || (!string.IsNullOrEmpty(series) && (other.SeriesId == series || other.ContentId == series));
        foreach (var row in Rows.Where(r => r.CanRemoveCards))
        {
            row.RemoveWhere(SameTitle);
        }
        if (Rows.Any(r => r.Cards.Count == 0))
        {
            Rows = [.. Rows.Where(r => r.Cards.Count > 0)];
            ShowLoaded();
        }
        FollowRowsWithHero();
    }

    // Other pages' rows change with a removal; these are changed in place below, and a reload of their own would only
    // move them under the pointer.
    private void ReportOwnChange()
    {
        if (_changes is null)
        {
            return;
        }
        _isReportingOwnChange = true;
        try
        {
            _changes.Report();
        }
        finally
        {
            _isReportingOwnChange = false;
        }
        if (Loading.IsCompleted)
        {
            _loadedVersion = _changes.Version;
        }
    }

    private CatalogItem? FindRemovable(MediaCard card) =>
        Rows.Where(r => r.CanRemoveCards).Select(r => r.ItemOf(card)).FirstOrDefault(i => i is not null);

    private Task RefreshIfNeededAsync()
    {
        if (_shownFor != SessionKey())
        {
            return Loading = LoadAsync(quietly: false);
        }
        if (!Loading.IsCompleted)
        {
            return Loading;
        }
        bool isStale = _loadedAt is not { } loadedAt || _time.GetUtcNow() - loadedAt >= StaleAfter
            || _loadedVersion != (_changes?.Version ?? 0);
        return isStale ? Loading = LoadAsync(quietly: !HasError && Rows.Count > 0) : Task.CompletedTask;
    }

    /// <param name="quietly">Keeps the shown rows until the new ones are there, and keeps them if the refresh fails.</param>
    private async Task LoadAsync(bool quietly)
    {
        _run?.Cancel();
        var run = new CancellationTokenSource();
        _run = run;
        var cancellationToken = run.Token;
        _shownFor = SessionKey();
        // Read before asking: a change while the answer is on its way leaves these rows stale.
        long version = _changes?.Version ?? 0;
        Notice = "";
        if (!quietly)
        {
            Rows = [];
            _loadedAt = null;
        }
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            // Signed out or between profiles: nothing to show and nothing to ask.
            Rows = [];
            IsLoading = false;
            ShowState(hasError: false, isEmpty: false, "");
            return;
        }

        if (Rows.Count == 0)
        {
            IsLoading = true;
            ShowState(hasError: false, isEmpty: false, LoadingText);
        }
        try
        {
            var images = await BrowseArtwork.ParametersAsync(_images, server, DisplayScale, _logger, cancellationToken);
            var collection = await _catalog.ListHomeSectionsAsync(server.BaseUri, images, cancellationToken);
            var (sections, isComplete) = await HydrateAsync(server.BaseUri, collection.Sections, images, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ShowRows(BuildRows(sections, server.BaseUri));
            // A row that could not be fetched is tried again on the next visit.
            _loadedAt = isComplete ? _time.GetUtcNow() : null;
            _loadedVersion = version;
            ShowLoaded();
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // A newer load replaced this one; its answer, or its failure, is stale.
                return;
            }
            _logger.LogWarning("Loading Home failed: {Failure}", ApiFailureText.Describe(ex));
            _loadedAt = null;
            if (Rows.Count == 0)
            {
                string text = BrowseFailureText.Describe(ex, "Home could not be loaded right now.");
                ErrorText = text;
                ShowState(hasError: true, isEmpty: false, text);
            }
        }
        finally
        {
            if (run == _run)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// Fetches the rows that came back without cards although they count some. A row that fails stays
    /// out; the result says whether every fetch answered.
    /// </summary>
    private async Task<(IReadOnlyList<Section> Sections, bool IsComplete)> HydrateAsync(
        Uri server, IReadOnlyList<Section> sections, ImageParameters images, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaxConcurrentRowRequests);
        var fetches = sections
            .Select(section => section.Items.Count == 0 && section.TotalCount > 0 && section.Id.Length > 0
                ? FetchRowAsync(server, section, images, gate, cancellationToken)
                : Task.FromResult<Section?>(section))
            .ToArray();
        var fetched = await Task.WhenAll(fetches);
        return ([.. fetched.OfType<Section>()], fetched.All(s => s is not null));
    }

    private async Task<Section?> FetchRowAsync(Uri server, Section section, ImageParameters images, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await _catalog.GetHomeSectionItemsAsync(server, section.Id, images, cancellationToken);
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            // Hidden or removed since the list was made: the row is simply gone.
            return section with { TotalCount = 0 };
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            _logger.LogWarning("Loading a Home row failed: {Failure}", ApiFailureText.Describe(ex));
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<HomeRowViewModel> BuildRows(IReadOnlyList<Section> sections, Uri server)
    {
        var rows = new List<HomeRowViewModel>();
        foreach (var section in sections.Where(s => s.TotalCount > 0))
        {
            var cards = section.Items
                .Where(item => item.ContentId.Length > 0 && HomeRowRules.Shows(section.SectionType, item))
                .Select(item => (MediaCardFactory.ForSection(section, item, server), item))
                .ToList();
            if (cards.Count == 0)
            {
                continue;
            }
            // "Explore all" on every row, as the web's, also when the row already holds all of it.
            var exploreAll = section.Id.Length > 0
                ? new RelayCommand(() => _navigation.NavigateTo(AppRoute.Section,
                    new SectionRouteParameter(section.Id, section.Title, SectionType: section.SectionType)))
                : null;
            rows.Add(new HomeRowViewModel(section, cards, OpenCardCommand, exploreAll, RemoveCommand, PointCardCommand));
        }
        return rows;
    }

    // A quiet reload keeps the rows that are still there and takes their new cards in place, so each keeps where it was
    // scrolled to; another set of rows replaces them.
    private void ShowRows(IReadOnlyList<HomeRowViewModel> fresh)
    {
        var shown = Rows;
        bool sameRows = shown.Count == fresh.Count && shown.Zip(fresh).All(pair =>
            pair.First.SectionId == pair.Second.SectionId && pair.First.Title == pair.Second.Title
            && (pair.First.ExploreAllCommand is null) == (pair.Second.ExploreAllCommand is null));
        if (!sameRows)
        {
            Rows = fresh;
            return;
        }
        foreach (var (row, next) in shown.Zip(fresh))
        {
            row.Take(next);
        }
        FollowRowsWithHero();
    }

    private void ShowLoaded()
    {
        bool isEmpty = Rows.Count == 0;
        ShowState(hasError: false, isEmpty, isEmpty ? EmptyText : "");
    }

    private void ShowState(bool hasError, bool isEmpty, string status)
    {
        HasError = hasError;
        IsEmpty = isEmpty;
        StatusText = status;
    }

    private (bool IsReady, string? ServerId, string? ProfileId) SessionKey() =>
        (_session.State == SessionState.Ready, _session.ActiveServer?.Id, _session.ProfileId);

    // Changed can come from a background thread (a sign-out by the authentication handler).
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.HasThreadAccess)
        {
            ReloadForNewSession();
        }
        else
        {
            _dispatcher.Post(ReloadForNewSession);
        }
    }

    // Raised on whichever thread made the change.
    private void OnViewerStateChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.HasThreadAccess)
        {
            ReloadForViewerState();
        }
        else
        {
            _dispatcher.Post(ReloadForViewerState);
        }
    }

    private void ReloadForViewerState()
    {
        if (_isActive && !_isReportingOwnChange && _shownFor == SessionKey())
        {
            Loading = LoadAsync(quietly: !HasError && Rows.Count > 0);
        }
    }

    private void ReloadForNewSession()
    {
        // Account updates also raise Changed; only another server or profile changes the rows.
        if (_isActive && _shownFor != SessionKey())
        {
            Loading = LoadAsync(quietly: false);
        }
    }
}
