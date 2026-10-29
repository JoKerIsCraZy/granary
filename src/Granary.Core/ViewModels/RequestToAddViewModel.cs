using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// "Request to add" under Search's results, as the web's <c>RequestToAddSection</c> shows it on the catalog search: the
/// movies and series TMDB finds for the text that the library does not have, 20 to a page with Previous and Next, while
/// the server takes requests. A text of one character is not searched. A first page that fails, or that is the only page
/// and holds nothing to add, leaves the section out; a later page that fails says so, with Retry and Back to page 1. A
/// card opens the library's title when the library has it, else the title's own page.
/// </summary>
/// <remarks>The commands take no cancellation token: a new text or page cancels the read in flight.</remarks>
public sealed partial class RequestToAddViewModel : ObservableObject
{
    /// <summary>The titles a page shows at most, as the web's grid.</summary>
    internal const int PageLimit = 20;

    /// <summary>The shortest text searched.</summary>
    internal const int MinQueryLength = 2;

    private readonly ISessionManager _session;
    private readonly RequestsApi _requests;
    private readonly RequestsFeature _feature;
    private readonly INavigationService _navigation;
    private readonly ILogger<RequestToAddViewModel> _logger;
    // Where each card opens: the library's title, or the title's own page.
    private readonly Dictionary<string, (AppRoute Route, object Parameter)> _destinations = new(StringComparer.Ordinal);
    private CancellationTokenSource? _run;
    private string _mediaType = "all";

    public RequestToAddViewModel(ISessionManager session, RequestsApi requests, RequestsFeature feature, INavigationService navigation,
        ILogger<RequestToAddViewModel> logger, CardRequestNotice? requestNotice = null)
    {
        _session = session;
        _requests = requests;
        _feature = feature;
        _navigation = navigation;
        _logger = logger;
        RequestNotice = requestNotice ?? CardRequestNotice.None;
    }

    /// <summary>A card's Request that failed, in the web's toast words; Search attaches it while it shows.</summary>
    public CardRequestNotice RequestNotice { get; }

    /// <summary>The text searched, trimmed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note))]
    public partial string Query { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageEmpty))]
    public partial IReadOnlyList<MediaCard> Cards { get; private set; } = [];

    /// <summary>The section is on the page: TMDB found titles to add, or has more pages to look through.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageEmpty))]
    public partial bool IsShown { get; private set; }

    /// <summary>A text's first page is being read; nothing is shown for it yet.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    /// <summary>Another page is being read: the shown one stays, dimmed, and the pager waits.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand), nameof(RetryPageCommand), nameof(FirstPageCommand))]
    public partial bool IsChangingPage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagePosition), nameof(ShowsPager), nameof(PageErrorText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int Page { get; private set; } = 1;

    /// <summary>The pages TMDB can be read to, at most 500; 0 while a page failed and the count is unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagePosition), nameof(ShowsPager))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial int TotalPages { get; private set; }

    /// <summary>"Page 2 of 9", or "Page 2" while the count is unknown.</summary>
    public string PagePosition => TotalPages > 0 ? $"Page {Page} of {TotalPages}" : $"Page {Page}";

    /// <summary>Previous and Next show while either leads somewhere.</summary>
    public bool ShowsPager => Page > 1 || Page < TotalPages;

    /// <summary>The library search found nothing for the text, which <see cref="Note"/> then says.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note))]
    public partial bool LibraryFoundNothing { get; set; }

    /// <summary>"Nothing in your library matches “heat”." under the heading, or empty.</summary>
    public string Note => LibraryFoundNothing && Query.Length > 0 ? $"Nothing in your library matches “{Query}”." : "";

    /// <summary>The shown page failed to load; the section offers Retry and Back to page 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageErrorText), nameof(IsPageEmpty))]
    public partial bool HasPageError { get; private set; }

    /// <summary>"Couldn’t load page 2." while <see cref="HasPageError"/>.</summary>
    public string PageErrorText => HasPageError ? $"Couldn’t load page {Page}." : "";

    /// <summary>Every title on the page is in the library already; other pages may hold some to add.</summary>
    public bool IsPageEmpty => IsShown && !HasPageError && Cards.Count == 0;

    /// <summary>The read in flight, for tests.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the first page for <paramref name="query"/>, dropping what was shown for the last text.</summary>
    public Task SearchAsync(string query, SearchScope scope)
    {
        Clear();
        Query = query.Trim();
        _mediaType = scope switch
        {
            SearchScope.Movies => "movie",
            SearchScope.Series => "series",
            _ => "all",
        };
        if (Query.Length < MinQueryLength)
        {
            return Loading = Task.CompletedTask;
        }
        IsSearching = true;
        return Loading = LoadAsync(1, Start());
    }

    /// <summary>No text, or another session: nothing shown, nothing in flight.</summary>
    public void Clear()
    {
        _run?.Cancel();
        _run = null;
        RequestNotice.Clear();
        Query = "";
        LibraryFoundNothing = false;
        IsSearching = false;
        IsChangingPage = false;
        Hide();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task PreviousPageAsync() => GoToAsync(Page - 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task NextPageAsync() => GoToAsync(Page + 1);

    [RelayCommand(CanExecute = nameof(CanChangePage))]
    private Task RetryPageAsync() => GoToAsync(Page);

    [RelayCommand(CanExecute = nameof(CanChangePage))]
    private Task FirstPageAsync() => GoToAsync(1);

    [RelayCommand]
    private void OpenCard(MediaCard? card)
    {
        if (card is not null && _destinations.TryGetValue(card.Id, out var destination))
        {
            _navigation.NavigateTo(destination.Route, destination.Parameter);
        }
    }

    private bool CanChangePage() => !IsChangingPage;

    private bool CanGoBack() => !IsChangingPage && Page > 1;

    private bool CanGoForward() => !IsChangingPage && Page < TotalPages;

    private Task GoToAsync(int page)
    {
        if (!IsShown || page < 1 || page > RequestsApi.TmdbMaxPage)
        {
            return Task.CompletedTask;
        }
        IsChangingPage = true;
        return Loading = LoadAsync(page, Start());
    }

    private CancellationTokenSource Start()
    {
        _run?.Cancel();
        var run = new CancellationTokenSource();
        _run = run;
        return run;
    }

    private async Task LoadAsync(int page, CancellationTokenSource run)
    {
        var cancellationToken = run.Token;
        try
        {
            if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
            {
                return;
            }
            // Read once per profile; a read in flight, the sidebar's, is waited for.
            await _feature.RefreshAsync().WaitAsync(cancellationToken);
            if (!_feature.IsEnabled)
            {
                return;
            }
            var found = await _requests.SearchAsync(server.BaseUri, Query, _mediaType, page, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Show(page, found);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Searching titles to request failed: {Failure}", ApiFailureText.Describe(ex));
            // A failed first page leaves the section out; a later one keeps it, so the viewer can retry or go back.
            if (page == 1)
            {
                Hide();
            }
            else
            {
                ShowFailedPage(page);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer text or page replaced this read.
        }
        finally
        {
            if (run == _run)
            {
                IsSearching = false;
                IsChangingPage = false;
            }
        }
    }

    private void Show(int page, RequestMediaPage found)
    {
        int totalPages = Math.Clamp(found.TotalPages, 0, RequestsApi.TmdbMaxPage);
        var titles = found.Results
            .Where(result => result.Availability != "available" && result.TmdbId > 0)
            .DistinctBy(RequestCards.IdOf)
            .Take(PageLimit)
            .ToList();
        // Any page can hold only titles the library has; it stays while TMDB has others, so the pager can reach them.
        if (titles.Count == 0 && page == 1 && totalPages <= 1)
        {
            Hide();
            return;
        }
        _destinations.Clear();
        var cards = new List<MediaCard>(titles.Count);
        foreach (var result in titles)
        {
            if (RequestCards.Destination(result) is { } destination)
            {
                var card = RequestCards.Of(result);
                _destinations[card.Id] = destination;
                cards.Add(card);
            }
        }
        HasPageError = false;
        TotalPages = totalPages;
        Page = page;
        Cards = cards;
        IsShown = true;
    }

    private void ShowFailedPage(int page)
    {
        _destinations.Clear();
        Cards = [];
        TotalPages = 0;
        Page = page;
        HasPageError = true;
        IsShown = true;
    }

    private void Hide()
    {
        IsShown = false;
        _destinations.Clear();
        Cards = [];
        HasPageError = false;
        TotalPages = 0;
        Page = 1;
    }
}
