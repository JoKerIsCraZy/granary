using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Titles from TMDB to request, as Silo's web shows them: a Discover row in full (<c>pages/RequestDiscoverSection.tsx</c>,
/// the hub's "Explore all"), or a studio's movies, a network's series or a genre's movies or series
/// (<c>pages/RequestBrowse.tsx</c>), sorted by popularity, rating or release date. A poster grid read page by page while
/// it scrolls, with Load more for the keyboard; a title an earlier page showed shows once, since TMDB lists shift
/// between reads. A card opens the library's title when the library has it, else the title's own page.
/// </summary>
public sealed partial class RequestTitlesViewModel : ObservableObject
{
    /// <summary>
    /// A page that adds nothing (a rating limit filtered every title on it) reads the next by itself this many times in a
    /// row; then Load more waits for the viewer.
    /// </summary>
    internal const int EmptyPagesInARow = 3;

    internal const string MoreFailedText = "Couldn’t load more titles.";

    private static readonly SettingChoice Popularity = new("popularity", "Popularity");
    private static readonly IReadOnlyList<SettingChoice> Sorts = [Popularity, new("vote_average", "Rating"), new("release_date", "Release date")];
    private static readonly SettingChoice Movies = new("movie", "Movies");
    private static readonly SettingChoice Series = new("series", "Series");

    private readonly ISessionManager _session;
    private readonly RequestsApi _requests;
    private readonly INavigationService _navigation;
    private readonly ILogger<RequestTitlesViewModel> _logger;
    // Where each card opens: the library's title, or the title's own page.
    private readonly Dictionary<string, (AppRoute Route, object Parameter)> _destinations = new(StringComparer.Ordinal);
    private RequestTitlesRouteParameter? _route;
    private (string? ServerId, string? ProfileId)? _loadedFor;
    private CancellationTokenSource _run = new();
    private int? _nextPage;
    // A Discover row answered with a page to ask for next; from then on, a page without one is the last.
    private bool _sawCursor;
    // Set before State, which tells the views.
    private bool _notFound;
    private bool _choosing;

    public RequestTitlesViewModel(ISessionManager session, RequestsApi requests, INavigationService navigation, ILogger<RequestTitlesViewModel> logger,
        CardRequestNotice? requestNotice = null)
    {
        _session = session;
        _requests = requests;
        _navigation = navigation;
        _logger = logger;
        RequestNotice = requestNotice ?? CardRequestNotice.None;
    }

    public ObservableCollection<MediaCard> Cards { get; } = [];

    /// <summary>A card's Request that failed, in the web's toast words.</summary>
    public CardRequestNotice RequestNotice { get; }

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>"Studio", "Network" or "Genre" under a browse page's name; "Loading..." while its first page loads.</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "";

    /// <summary>A studio's or network's logo beside its name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogo))]
    public partial Uri? Logo { get; private set; }

    public bool HasLogo => Logo is not null;

    /// <summary>The name in the tile beside it, when there is no logo; empty while the first page loads.</summary>
    [ObservableProperty]
    public partial string TileText { get; private set; } = "";

    /// <summary>A studio, network or genre, not a Discover row: the tile, the sort, and for a genre Movies and Series.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeaderTile), nameof(IsGenre))]
    public partial DiscoverBrowseKind? Browse { get; private set; }

    public bool IsGenre => Browse == DiscoverBrowseKind.Genre;

    /// <summary>Popularity, Rating, Release date: a browse page's sorts. The same list always, so a select bound to it never loses its choice.</summary>
    public IReadOnlyList<SettingChoice> SortChoices { get; } = Sorts;

    [ObservableProperty]
    public partial SettingChoice SelectedSort { get; set; } = Popularity;

    /// <summary>Movies and Series, a genre's media types.</summary>
    public IReadOnlyList<SettingChoice> MediaChoices { get; } = [Movies, Series];

    [ObservableProperty]
    public partial SettingChoice SelectedMedia { get; set; } = Movies;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(HasError), nameof(IsReady), nameof(IsNotFound), nameof(HasHeaderTile),
        nameof(HasChoices))]
    public partial LibraryLoadState State { get; private set; }

    /// <summary>What the page says instead of titles: why it failed, that it does not exist, or that nothing matched.</summary>
    [ObservableProperty]
    public partial string Message { get; private set; } = "";

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool IsEmpty => State == LibraryLoadState.Empty;

    public bool HasError => State == LibraryLoadState.Error && !_notFound;

    /// <summary>The row or the studio, network or genre does not exist: no retry, no sort.</summary>
    public bool IsNotFound => State == LibraryLoadState.Error && _notFound;

    public bool IsReady => State == LibraryLoadState.Ready;

    public bool HasHeaderTile => Browse is not null && !IsNotFound;

    public bool HasChoices => Browse is not null && !IsNotFound && State != LibraryLoadState.Idle;

    /// <summary>Another page is there to read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLoadMore))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool CanLoadMore { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLoadMore))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoadingMore { get; private set; }

    /// <summary>The last read of a later page failed: Try again reads it again; scrolling no longer does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLoadMore))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMoreError { get; private set; }

    public bool ShowsLoadMore => CanLoadMore && !IsLoadingMore && !HasMoreError;

    /// <summary>
    /// Shows a row or a studio, network or genre. <paramref name="isReturn"/> (Back) keeps the same one for the same
    /// profile, with its sort and the pages read; otherwise it is read again, sorted by popularity.
    /// </summary>
    public Task ShowAsync(RequestTitlesRouteParameter? route, bool isReturn = false)
    {
        if (route is null)
        {
            return Task.CompletedTask;
        }
        RequestNotice.Attach();
        bool current = route == _route && _loadedFor == Key() && State is LibraryLoadState.Ready or LibraryLoadState.Empty or LibraryLoadState.Loading;
        if (isReturn && current)
        {
            return Task.CompletedTask;
        }
        _route = route;
        _choosing = true;
        try
        {
            Browse = route.Browse;
            SelectedSort = Popularity;
            SelectedMedia = route.MediaType == "series" ? Series : Movies;
        }
        finally
        {
            _choosing = false;
        }
        return LoadAsync(fresh: true);
    }

    /// <summary>The page goes: its cards' requests are no longer its to tell.</summary>
    public void Deactivate() => RequestNotice.Detach();

    [RelayCommand]
    private Task RetryAsync() => LoadAsync(fresh: false);

    /// <summary>Reads the next page, when the end of the grid comes into view or Load more is pressed.</summary>
    [RelayCommand(CanExecute = nameof(ShowsLoadMore))]
    private Task LoadMoreAsync() => _nextPage is int page ? ReadAsync(page, _run.Token) : Task.CompletedTask;

    [RelayCommand]
    private Task TryAgainAsync()
    {
        HasMoreError = false;
        return LoadMoreAsync();
    }

    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null && _destinations.TryGetValue(card.Id, out var destination))
        {
            _navigation.NavigateTo(destination.Route, destination.Parameter);
        }
    }

    partial void OnSelectedSortChanged(SettingChoice oldValue, SettingChoice newValue) => Rechoose(oldValue, newValue);

    partial void OnSelectedMediaChanged(SettingChoice oldValue, SettingChoice newValue) => Rechoose(oldValue, newValue);

    // Another sort or media type lists other titles from the first page; a cleared choice changes nothing.
    private void Rechoose(SettingChoice? oldValue, SettingChoice? newValue)
    {
        if (!_choosing && newValue is not null && oldValue is not null && newValue != oldValue && _route is { Browse: not null })
        {
            _ = LoadAsync(fresh: false);
        }
    }

    // A new row or studio starts from its route's name; another sort, or a retry, keeps the name and logo shown.
    private async Task LoadAsync(bool fresh)
    {
        var previous = _run;
        _run = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _run.Token;
        Cards.Clear();
        _destinations.Clear();
        RequestNotice.Clear();
        _nextPage = null;
        _sawCursor = false;
        CanLoadMore = IsLoadingMore = HasMoreError = false;
        _notFound = false;
        Message = "";
        if (_route is not { } route || _session is not { State: SessionState.Ready, ActiveServer: not null })
        {
            _loadedFor = null;
            State = LibraryLoadState.Idle;
            return;
        }
        if (fresh)
        {
            Title = route.Title.Length > 0 ? route.Title : FallbackTitle(route);
            Subtitle = route.Browse is null ? "" : "Loading...";
            Logo = null;
            TileText = "";
        }
        _loadedFor = Key();
        State = LibraryLoadState.Loading;
        await ReadAsync(1, token);
    }

    // Reads a page and adds its titles. A page that adds nothing reads the next one, a few times in a row: the web's
    // foot keeps loading while it is in view, and an empty grid has no end to scroll to.
    private async Task ReadAsync(int page, CancellationToken token)
    {
        if (_route is not { } route || _session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        bool first = page == 1;
        IsLoadingMore = !first;
        try
        {
            for (int empty = 0; ; empty++)
            {
                var answer = await ReadPageAsync(server.BaseUri, route, page, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }
                if (page == 1)
                {
                    ShowHeader(route, answer);
                }
                int? next = route.Browse is null ? NextSectionPage(answer.Cursor, page, answer.TotalPages)
                    : page < PageCount(answer.TotalPages) ? page + 1 : null;
                int added = Add(answer.Results);
                _nextPage = next;
                CanLoadMore = next is not null;
                State = Cards.Count == 0 && next is null ? LibraryLoadState.Empty : LibraryLoadState.Ready;
                if (State == LibraryLoadState.Empty)
                {
                    Message = route.Browse is null ? "Nothing here right now." : "Nothing matched. Try a different sort.";
                }
                if (added > 0 || next is not int following || empty + 1 >= EmptyPagesInARow)
                {
                    break;
                }
                page = following;
                IsLoadingMore = true;
            }
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                // Another row, sort or media type took over.
                return;
            }
            Log(ex);
            if (State == LibraryLoadState.Loading)
            {
                ShowFailure(route, ex);
            }
            else
            {
                HasMoreError = true;
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoadingMore = false;
            }
        }
    }

    // Only reads: what it answers shows once the caller knows the read is still the page's.
    private async Task<TitlesPage> ReadPageAsync(Uri server, RequestTitlesRouteParameter route, int page, CancellationToken token)
    {
        if (route.Browse is not { } kind)
        {
            var section = await _requests.GetDiscoverSectionAsync(server, route.Key, page, token);
            return new TitlesPage(section.Results, section.TotalPages, section.NextPage, section.Title, null);
        }
        // A select can clear its choice while its list changes; the web falls back the same way.
        string? mediaType = kind == DiscoverBrowseKind.Genre ? SelectedMedia?.Value ?? Movies.Value : null;
        var browse = await _requests.BrowseAsync(server, kind, route.Key, SelectedSort?.Value ?? Popularity.Value, page, mediaType, token);
        return new TitlesPage(browse.Results, browse.TotalPages, null, browse.DisplayName, browse.LogoUrl);
    }

    private void ShowHeader(RequestTitlesRouteParameter route, TitlesPage answer)
    {
        if (answer.Name.Length > 0)
        {
            Title = answer.Name;
        }
        if (route.Browse is { } kind)
        {
            Subtitle = KindLabel(kind);
            Logo = kind == DiscoverBrowseKind.Genre ? null : TmdbImages.Logo(answer.LogoUrl);
            TileText = Title;
        }
    }

    // The web's rule: the row's own next page once it named one (a rating limit made the server read several TMDB pages
    // to fill one, so page + 1 would repeat titles), else numbered pages up to TMDB's last.
    private int? NextSectionPage(int? cursor, int page, int totalPages)
    {
        if (cursor is int next and not 0)
        {
            _sawCursor = true;
            if (next > page && next <= RequestsApi.TmdbMaxPage)
            {
                return next;
            }
        }
        return _sawCursor || page >= PageCount(totalPages) ? null : page + 1;
    }

    private static int PageCount(int totalPages) => Math.Clamp(totalPages, 0, RequestsApi.TmdbMaxPage);

    // The titles an earlier page did not show; how many were new.
    private int Add(IReadOnlyList<RequestMediaResult> results)
    {
        int added = 0;
        foreach (var result in results.Where(r => r.TmdbId > 0))
        {
            var card = RequestCards.Of(result);
            if (_destinations.ContainsKey(card.Id) || RequestCards.Destination(result) is not { } destination)
            {
                continue;
            }
            _destinations.Add(card.Id, destination);
            Cards.Add(card);
            added++;
        }
        return added;
    }

    // A row or a studio the server does not know says so, without a retry; anything else is TMDB or Silo failing.
    private void ShowFailure(RequestTitlesRouteParameter route, Exception ex)
    {
        if (route.Browse is { } kind)
        {
            _notFound = ex is ArgumentException or SiloApiException { Status: 404 };
            Title = _notFound ? $"{KindLabel(kind)} not found." : Title;
            Subtitle = _notFound ? "" : KindLabel(kind);
            Message = _notFound ? "" : "Could not load this browse page. Try a different sort or media type.";
        }
        else
        {
            _notFound = ex is ArgumentException or SiloApiException { Status: 400 or 404 or 422 };
            Title = _notFound ? "Not found" : Title;
            Message = _notFound ? "This Discover row doesn’t exist." : "TMDB couldn’t be reached. Try again in a moment.";
        }
        State = LibraryLoadState.Error;
    }

    private static string KindLabel(DiscoverBrowseKind kind) => kind switch
    {
        DiscoverBrowseKind.Studio => "Studio",
        DiscoverBrowseKind.Network => "Network",
        _ => "Genre",
    };

    // The web's names until the server's arrive: "trending_movies" is "Trending movies", "marvel-studios" "Marvel Studios".
    private static string FallbackTitle(RequestTitlesRouteParameter route)
    {
        if (route.Browse is not null)
        {
            return string.Join(' ', route.Key.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(Capitalized));
        }
        string words = string.Join(' ', route.Key.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries));
        return words.Length > 0 ? Capitalized(words) : "Discover";
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private void Log(Exception ex)
    {
        // A key or slug no route takes is the page's "not found", not a fault.
        if (LibraryErrors.IsExpected(ex) || ex is ArgumentException)
        {
            _logger.LogWarning(ex, "Could not load titles to request");
        }
        else
        {
            // Still the page's to show: a spinner that never ends would hide it.
            _logger.LogError(ex, "Titles to request could not be shown");
        }
    }

    private (string? ServerId, string? ProfileId) Key() => (_session.ActiveServer?.Id, _session.ProfileId);
}

/// <summary>A page of titles as read, before it shows.</summary>
/// <param name="Cursor">A Discover row's <c>next_page</c>.</param>
internal sealed record TitlesPage(IReadOnlyList<RequestMediaResult> Results, int TotalPages, int? Cursor, string Name, string? LogoUrl);
