using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Home: the server's rows as Silo's web client shows them (docs/api-contract.md, 7.1). Rows that came
/// back empty but count cards are fetched again, at most four at a time as Android does; rows without
/// cards are hidden, and Continue Watching keeps only video cards. The page calls
/// <see cref="ActivateAsync"/> when it is shown and <see cref="Deactivate"/> when it leaves: another
/// server or profile reloads at once, a return reloads only rows older than <see cref="StaleAfter"/>.
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

    private CancellationTokenSource? _run;
    private (bool IsReady, string? ServerId, string? ProfileId)? _shownFor;
    private DateTimeOffset? _loadedAt;
    private bool _isActive;

    public HomeViewModel(
        ISessionManager session,
        CatalogApi catalog,
        ImageParameterCache images,
        INavigationService navigation,
        IUiDispatcher dispatcher,
        TimeProvider time,
        ILogger<HomeViewModel> logger)
    {
        _session = session;
        _catalog = catalog;
        _images = images;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    [ObservableProperty]
    public partial IReadOnlyList<HomeRowViewModel> Rows { get; private set; } = [];

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

    /// <summary>
    /// Removes the card's title from Continue Watching and Next Up. The server drops the whole show from
    /// both rows, so every card of that series goes locally too.
    /// </summary>
    [RelayCommand]
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
        bool isStale = _loadedAt is not { } loadedAt || _time.GetUtcNow() - loadedAt >= StaleAfter;
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
            Rows = BuildRows(sections, server.BaseUri);
            // A row that could not be fetched is tried again on the next visit.
            _loadedAt = isComplete ? _time.GetUtcNow() : null;
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
            // "Explore all" only when the server has more than the row holds.
            var exploreAll = section.TotalCount > section.Items.Count && section.Id.Length > 0
                ? new RelayCommand(() => _navigation.NavigateTo(AppRoute.Section,
                    new SectionRouteParameter(section.Id, section.Title, SectionType: section.SectionType)))
                : null;
            rows.Add(new HomeRowViewModel(section, cards, OpenCardCommand, exploreAll));
        }
        return rows;
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

    private void ReloadForNewSession()
    {
        // Account updates also raise Changed; only another server or profile changes the rows.
        if (_isActive && _shownFor != SessionKey())
        {
            Loading = LoadAsync(quietly: false);
        }
    }
}
