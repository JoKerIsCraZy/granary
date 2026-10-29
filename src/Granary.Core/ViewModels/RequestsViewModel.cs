using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

public enum RequestsTab
{
    Discover,
    Yours,
}

/// <summary>A Discover row of titles from TMDB.</summary>
/// <param name="OpenCommand">Takes a card.</param>
/// <param name="ExploreAllCommand">The row in full; null for a row of one page.</param>
public sealed record RequestDiscoverRow(string Title, IReadOnlyList<MediaCard> Cards, ICommand OpenCommand, ICommand? ExploreAllCommand = null);

/// <summary>
/// The Requests hub, as Silo's web shows it (<c>pages/Requests.tsx</c>): Discover, rows of titles from TMDB with their
/// request state, each in full behind Explore all, then the studios, networks and genres to browse; and Yours, the account's requests (up to 100) grouped by what happens next: Needs attention, On the
/// way, In your library, Cancelled, each in the server's order. A request can be cancelled while nothing was sent for
/// it; one that arrived opens in the library. While the page shows and a request is downloading, Yours is read again
/// every <see cref="DownloadPollInterval"/>, quietly behind the rows shown, as the web does.
/// </summary>
public sealed partial class RequestsViewModel : ObservableObject
{
    /// <summary>The <see cref="MediaCard.Type"/> of a title from TMDB: no card actions.</summary>
    public const string CardType = "request";

    /// <summary>How many of the account's requests Yours shows, as the web.</summary>
    public const int MineShown = 100;

    internal const string CancelFailedText = "Couldn't cancel the request. Try again.";

    /// <summary>How often Yours is read again while a request downloads, and how old it may be when the page shows again.</summary>
    public static readonly TimeSpan DownloadPollInterval = TimeSpan.FromSeconds(30);

    private static readonly (string Key, string Title)[] Groups =
        [("attention", "Needs attention"), ("on_the_way", "On the way"), ("in_library", "In your library"), ("cancelled", "Cancelled")];

    private readonly ISessionManager _session;
    private readonly RequestsApi _requests;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<RequestsViewModel> _logger;
    private readonly MediaCardActions? _cardActions;
    private bool _hearsCards;
    // Where each Discover card opens: the library's title, or the title's own page.
    private readonly Dictionary<string, (AppRoute Route, object Parameter)> _destinations = new(StringComparer.Ordinal);
    private (string? ServerId, string? ProfileId)? _loadedFor;
    private CancellationTokenSource _run = new();
    // Each read of Yours; an answer shows only while its read is the latest.
    private int _mineReads;
    private DateTimeOffset _mineReadAt = DateTimeOffset.MinValue;
    private bool _downloading;
    private ITimer? _poll;

    public RequestsViewModel(ISessionManager session, RequestsApi requests, INavigationService navigation, TimeProvider time,
        IUiDispatcher dispatcher, ILogger<RequestsViewModel> logger, MediaCardActions? cardActions = null, CardRequestNotice? requestNotice = null)
    {
        _session = session;
        _requests = requests;
        _navigation = navigation;
        _time = time;
        _dispatcher = dispatcher;
        _logger = logger;
        _cardActions = cardActions;
        RequestNotice = requestNotice ?? CardRequestNotice.None;
        BrandRows =
        [
            new(DiscoverBrowseKind.Studio, "Studios", RetryBrandsAsync),
            new(DiscoverBrowseKind.Network, "Networks", RetryBrandsAsync),
            new(DiscoverBrowseKind.Genre, "Genres", RetryBrandsAsync),
        ];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiscoverTab), nameof(IsYoursTab))]
    public partial RequestsTab Tab { get; set; }

    public bool IsDiscoverTab => Tab == RequestsTab.Discover;

    public bool IsYoursTab => Tab == RequestsTab.Yours;

    public ObservableCollection<RequestDiscoverRow> DiscoverRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiscoverLoading), nameof(HasDiscoverError), nameof(IsDiscoverEmpty))]
    public partial LibraryLoadState DiscoverState { get; private set; }

    public bool IsDiscoverEmpty => DiscoverState == LibraryLoadState.Empty;

    public bool IsDiscoverLoading => DiscoverState == LibraryLoadState.Loading;

    public bool HasDiscoverError => DiscoverState == LibraryLoadState.Error;

    /// <summary>Studios, Networks and Genres, under the Discover rows.</summary>
    public IReadOnlyList<RequestBrandRowViewModel> BrandRows { get; }

    public ObservableCollection<RequestGroupViewModel> RequestGroups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsYoursLoading), nameof(HasYoursError), nameof(IsYoursEmpty), nameof(IsYoursReady))]
    public partial LibraryLoadState YoursState { get; private set; }

    public bool IsYoursReady => YoursState == LibraryLoadState.Ready;

    public bool IsYoursLoading => YoursState == LibraryLoadState.Loading;

    public bool HasYoursError => YoursState == LibraryLoadState.Error;

    public bool IsYoursEmpty => YoursState == LibraryLoadState.Empty;

    /// <summary>The count beside Yours; empty without requests.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasYoursCount))]
    public partial string YoursCount { get; private set; } = "";

    public bool HasYoursCount => YoursCount.Length > 0;

    /// <summary>A cancellation that failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; private set; } = "";

    public bool HasNotice => Notice.Length > 0;

    /// <summary>A Discover card's Request that failed, in the web's toast words; a request that went through shows on its card.</summary>
    public CardRequestNotice RequestNotice { get; }

    /// <summary>What each state means, as the web's "What the statuses mean".</summary>
    public IReadOnlyList<RequestStatusHelp> StatusHelp { get; } =
    [
        Help(RequestStates.Pending, "Waiting for an admin. You can cancel it until it is sent to the download automation."),
        Help(RequestStates.Approved, "Approved, not yet sent to the download automation. You can still cancel it."),
        Help(RequestStates.Processing, "Sent to the download automation."),
        Help(RequestStates.PartiallyAvailable, "Some episodes of the requested seasons have arrived."),
        Help(RequestStates.Available, "In your library and ready to watch."),
        Help(RequestStates.Declined, "An admin declined it."),
        Help(RequestStates.Cancelled, "Withdrawn before it was sent to the download automation."),
        Help(RequestStates.Failed, "Silo or the download automation hit an error."),
    ];

    /// <summary>
    /// Shows the hub: Back keeps it for the same profile, reading Yours again quietly when it is older than
    /// <see cref="DownloadPollInterval"/>; otherwise both tabs are read again. Downloads are followed until
    /// <see cref="Deactivate"/>.
    /// </summary>
    public Task ActivateAsync(bool isReturn)
    {
        _poll ??= _time.CreateTimer(_ => _dispatcher.Post(OnPoll), null, DownloadPollInterval, DownloadPollInterval);
        RequestNotice.Attach();
        if (_cardActions is not null && !_hearsCards)
        {
            _hearsCards = true;
            _cardActions.RequestAnswered += OnCardRequestAnswered;
        }
        bool shown = _loadedFor == Key() && DiscoverState != LibraryLoadState.Idle && YoursState != LibraryLoadState.Idle;
        if (!isReturn || !shown)
        {
            return LoadAsync();
        }
        return _time.GetUtcNow() - _mineReadAt >= DownloadPollInterval ? RefreshYoursAsync() : Task.CompletedTask;
    }

    /// <summary>Stops following downloads, and the cards' requests, when the page goes.</summary>
    public void Deactivate()
    {
        _poll?.Dispose();
        _poll = null;
        RequestNotice.Detach();
        if (_hearsCards)
        {
            _hearsCards = false;
            _cardActions!.RequestAnswered -= OnCardRequestAnswered;
        }
    }

    // A card's Request that went through (on any thread) is read into Yours, as the web reads its requests again; one that
    // failed is the notice's.
    private void OnCardRequestAnswered(object? sender, CardRequestAnswer answer) => _dispatcher.Post(() =>
    {
        if (_hearsCards && answer.Succeeded)
        {
            _ = RefreshYoursAsync();
        }
    });

    [RelayCommand]
    private void SelectTab(RequestsTab tab) => Tab = tab;

    [RelayCommand]
    private Task RetryDiscoverAsync() => _session is { State: SessionState.Ready, ActiveServer: { } server }
        ? LoadDiscoverAsync(server.BaseUri, _run.Token)
        : Task.CompletedTask;

    [RelayCommand]
    private Task RetryYoursAsync() => _session is { State: SessionState.Ready, ActiveServer: { } server }
        ? LoadMineAsync(server.BaseUri, _run.Token, quiet: false)
        : Task.CompletedTask;

    /// <summary>A Discover title: the library's when it has the title, else the title's own page.</summary>
    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null && _destinations.TryGetValue(card.Id, out var destination))
        {
            _navigation.NavigateTo(destination.Route, destination.Parameter);
        }
    }

    /// <summary>A studio's, network's or genre's titles.</summary>
    [RelayCommand]
    private void OpenBrand(RequestBrandTile? tile)
    {
        if (tile is not null)
        {
            _navigation.NavigateTo(AppRoute.RequestTitles, tile.Destination);
        }
    }

    [RelayCommand]
    private void OpenInLibrary(RequestRowViewModel? row)
    {
        if (row?.LibraryContentId is { } contentId)
        {
            _navigation.NavigateTo(AppRoute.Item, contentId);
        }
    }

    /// <summary>
    /// Withdraws a request the viewer confirmed. The list is read again after it either way, quietly: a refusal usually
    /// means the request moved on (it was sent meanwhile), and the row should say so.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CancelAsync(RequestRowViewModel? row)
    {
        if (row is not { CanCancel: true, IsCancelling: false } || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        row.IsCancelling = true;
        Notice = "";
        try
        {
            await _requests.CancelAsync(server.BaseUri, row.Id);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not cancel a request");
            Notice = CancelFailedText;
            row.IsCancelling = false;
        }
        await LoadMineAsync(server.BaseUri, _run.Token, quiet: true);
    }

    private Task RefreshYoursAsync() => _session is { State: SessionState.Ready, ActiveServer: { } server }
        ? LoadMineAsync(server.BaseUri, _run.Token, quiet: true)
        : Task.CompletedTask;

    private void OnPoll()
    {
        if (_downloading && _poll is not null && YoursState is LibraryLoadState.Ready or LibraryLoadState.Empty)
        {
            _ = RefreshYoursAsync();
        }
    }

    private async Task LoadAsync()
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _run.Token;
        Notice = "";
        RequestNotice.Clear();
        bool another = _loadedFor != Key();
        if (another || _session is not { State: SessionState.Ready })
        {
            // Nothing of another profile's shows, not even its count.
            RequestGroups.Clear();
            YoursCount = "";
            _downloading = false;
        }
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _loadedFor = null;
            DiscoverRows.Clear();
            DiscoverState = YoursState = LibraryLoadState.Idle;
            foreach (var row in BrandRows)
            {
                row.Tiles.Clear();
                row.State = LibraryLoadState.Idle;
            }
            return;
        }
        _loadedFor = Key();
        await Task.WhenAll([LoadDiscoverAsync(server.BaseUri, token), LoadMineAsync(server.BaseUri, token, quiet: false),
            .. BrandRows.Select(row => LoadBrandsAsync(row, server.BaseUri, token))]);
    }

    private async Task LoadDiscoverAsync(Uri server, CancellationToken token)
    {
        DiscoverRows.Clear();
        _destinations.Clear();
        DiscoverState = LibraryLoadState.Loading;
        try
        {
            var discover = await _requests.ListDiscoverAsync(server, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            foreach (var section in discover.Items)
            {
                var cards = section.Results.Where(r => r.TmdbId > 0).Select(CardOf).DistinctBy(c => c.Id, StringComparer.Ordinal).ToList();
                if (cards.Count > 0)
                {
                    DiscoverRows.Add(new RequestDiscoverRow(section.Title, cards, OpenCardCommand, ExploreAll(section)));
                }
            }
            DiscoverState = DiscoverRows.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }
            Log(ex, "Could not load the request discovery rows");
            DiscoverState = LibraryLoadState.Error;
        }
    }

    // The web offers a row in full when it has more than its first page.
    private RelayCommand? ExploreAll(DiscoverSection section)
    {
        if (section.TotalPages <= 1 || section.Key.Length == 0)
        {
            return null;
        }
        var route = new RequestTitlesRouteParameter(section.Key, section.Title);
        return new RelayCommand(() => _navigation.NavigateTo(AppRoute.RequestTitles, route));
    }

    private Task RetryBrandsAsync(RequestBrandRowViewModel row) => _session is { State: SessionState.Ready, ActiveServer: { } server }
        ? LoadBrandsAsync(row, server.BaseUri, _run.Token)
        : Task.CompletedTask;

    private async Task LoadBrandsAsync(RequestBrandRowViewModel row, Uri server, CancellationToken token)
    {
        row.Tiles.Clear();
        row.State = LibraryLoadState.Loading;
        try
        {
            var brands = await _requests.ListBrandsAsync(server, row.Kind, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            foreach (var brand in brands.Items.DistinctBy(b => b.Slug, StringComparer.Ordinal))
            {
                if (RequestBrandTiles.Of(row.Kind, brand, OpenBrandCommand) is { } tile)
                {
                    row.Tiles.Add(tile);
                }
            }
            row.State = row.Tiles.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }
            Log(ex, "Could not load the studios, networks or genres to browse");
            row.State = LibraryLoadState.Error;
        }
    }

    // The web shows up to 100 of the account's requests, which take two pages. A quiet read keeps the rows shown until
    // the answer replaces them, and keeps them when it fails.
    private async Task LoadMineAsync(Uri server, CancellationToken token, bool quiet)
    {
        int read = ++_mineReads;
        bool shown = YoursState is LibraryLoadState.Ready or LibraryLoadState.Empty;
        if (!quiet || !shown)
        {
            YoursState = LibraryLoadState.Loading;
        }
        try
        {
            var requests = new List<MediaRequest>();
            string? cursor = null;
            do
            {
                var page = await _requests.ListMineAsync(server, Math.Min(RequestsApi.MaxPageSize, MineShown - requests.Count), cursor,
                    cancellationToken: token);
                requests.AddRange(page.Items);
                cursor = page.Page is { HasMore: true, NextCursor: { Length: > 0 } next } && next != cursor ? next : null;
            }
            while (cursor is not null && requests.Count < MineShown);
            if (token.IsCancellationRequested || read != _mineReads)
            {
                // A later read, or a reload for another profile, shows its own answer.
                return;
            }
            var mine = requests.Where(r => r.Id.Length > 0).DistinctBy(r => r.Id, StringComparer.Ordinal).ToList();
            ShowMine(mine);
            _mineReadAt = _time.GetUtcNow();
            _downloading = mine.Any(r => r.Download is not null);
            YoursState = RequestGroups.Count == 0 ? LibraryLoadState.Empty : LibraryLoadState.Ready;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || read != _mineReads)
            {
                return;
            }
            Log(ex, "Could not load the account's requests");
            if (quiet && shown)
            {
                return;
            }
            RequestGroups.Clear();
            YoursCount = "";
            _downloading = false;
            YoursState = LibraryLoadState.Error;
        }
    }

    // The server's order (newest first) holds within each group, so a row does not jump when only its state changes.
    private void ShowMine(List<MediaRequest> requests)
    {
        RequestGroups.Clear();
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var rows = requests.Select(r => (Group: GroupOf(r), Row: RowOf(r, now, zone))).ToList();
        foreach (var (key, title) in Groups)
        {
            var members = rows.Where(r => r.Group == key).Select(r => r.Row).ToList();
            if (members.Count > 0)
            {
                RequestGroups.Add(new RequestGroupViewModel(title, members));
            }
        }
        YoursCount = requests.Count > 0 ? requests.Count.ToString(CultureInfo.InvariantCulture) : "";
    }

    private static string GroupOf(MediaRequest request) => RequestText.DisplayState(request.Status, request.Outcome, request.State) switch
    {
        RequestStates.Declined or RequestStates.Failed => "attention",
        RequestStates.Cancelled => "cancelled",
        // A season request lands only when all its seasons have; a download not scanned in yet is still on its way.
        RequestStates.Available => "in_library",
        _ => "on_the_way",
    };

    private static RequestRowViewModel RowOf(MediaRequest request, DateTimeOffset now, TimeZoneInfo zone)
    {
        string? state = RequestText.DisplayState(request.Status, request.Outcome, request.State);
        var details = new List<string> { RequestText.MediaTypeLabel(request.MediaType) };
        if (request.Year is int year and > 0)
        {
            details.Add(year.ToString(CultureInfo.InvariantCulture));
        }
        if (request.Seasons.Count > 0)
        {
            details.Add(RequestText.SeasonList(request.Seasons));
        }
        if (state == RequestStates.PartiallyAvailable && RequestText.SeasonProgress(request.SeasonProgress) is { Length: > 0 } progress)
        {
            details.Add(progress);
        }
        string dates = $"Requested {RequestText.Date(request.CreatedAt, zone)}";
        if (RequestText.LastChange(request, now, zone) is { } changed)
        {
            dates += $" · Updated {changed}";
        }
        bool isError = !string.IsNullOrWhiteSpace(request.LastError);
        return new RequestRowViewModel(request.Id, request.Title)
        {
            PosterUrl = TmdbImages.Poster(request.PosterPath, "w154"),
            StateLabel = state is null ? "" : RequestText.StateLabel(state),
            StateLook = state is null ? RequestBadgeLook.Outline : RequestText.Look(state),
            Details = string.Join(" · ", details),
            DownloadText = request.Download is { } download ? RequestText.Download(download, now) : "",
            DownloadPercent = request.Download is { } bar ? RequestText.DownloadPercent(bar) : null,
            Dates = dates,
            Note = isError ? request.LastError! : string.IsNullOrWhiteSpace(request.OutcomeReason) ? "" : $"Reason: {request.OutcomeReason}",
            IsError = isError,
            LibraryContentId = string.IsNullOrWhiteSpace(request.LibraryContentId) ? null : request.LibraryContentId,
            CanCancel = RequestText.CanCancel(request),
        };
    }

    private MediaCard CardOf(RequestMediaResult result)
    {
        var card = RequestCards.Of(result);
        if (RequestCards.Destination(result) is { } destination)
        {
            _destinations.TryAdd(card.Id, destination);
        }
        return card;
    }

    private static RequestStatusHelp Help(string state, string description) => new(RequestText.StateLabel(state), RequestText.Look(state), description);

    private void Log(Exception ex, string message)
    {
        if (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "{Message}", message);
        }
        else
        {
            _logger.LogError(ex, "{Message}", message);
        }
    }

    private (string? ServerId, string? ProfileId) Key() => (_session.ActiveServer?.Id, _session.ProfileId);
}
