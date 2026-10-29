using System.Collections.ObjectModel;
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
/// movies and series TMDB finds for the text that the library does not have, while the server takes requests. A text of
/// one character is not searched. Unlike the web's grid, which pages TMDB's twenty a page with Previous and Next even
/// when the library's titles leave six of them, the titles gather in one grid: a read takes TMDB's pages until a
/// screenful is there (<see cref="Fill"/>) or <see cref="PagesPerRead"/> pages were read, and the grid's end coming into
/// view (or Load more) reads on, to TMDB's last page. A title shows once. A first read that fails, or that finds nothing
/// to add on TMDB's only pages, leaves the section out; a later one that fails keeps what shows and offers Try again. A
/// card opens the library's title when the library has it, else the title's own page.
/// </summary>
/// <remarks>The commands take no cancellation token: a new text cancels the read in flight.</remarks>
public sealed partial class RequestToAddViewModel : ObservableObject
{
    /// <summary>A screenful: a read goes on to TMDB's next page until this many titles came, as the web's grid of twenty.</summary>
    internal const int Fill = 20;

    /// <summary>The pages one read takes at most, so a text whose pages the library mostly has does not read on and on.</summary>
    internal const int PagesPerRead = 3;

    /// <summary>The shortest text searched.</summary>
    internal const int MinQueryLength = 2;

    internal const string MoreFailedText = "Couldn’t load more titles.";

    private readonly ISessionManager _session;
    private readonly RequestsApi _requests;
    private readonly RequestsFeature _feature;
    private readonly INavigationService _navigation;
    private readonly ILogger<RequestToAddViewModel> _logger;
    // Where each card opens: the library's title, or the title's own page; the titles shown, so each shows once.
    private readonly Dictionary<string, (AppRoute Route, object Parameter)> _destinations = new(StringComparer.Ordinal);
    // What Search's backdrop shows of each card.
    private readonly Dictionary<string, BrowseHeroTitle> _backdrops = new(StringComparer.Ordinal);
    private CancellationTokenSource? _run;
    private string _mediaType = "all";
    private int? _nextPage;
    // A read is on its way: another never starts beside it, whatever asks.
    private bool _reading;

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

    /// <summary>The titles found so far, in TMDB's order; later reads add to the end.</summary>
    public ObservableCollection<MediaCard> Cards { get; } = [];

    /// <summary>The section is on the page: TMDB found titles to add, or has more pages to look through.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptySoFar))]
    public partial bool IsShown { get; private set; }

    /// <summary>A text's first read is on its way; nothing is shown for it yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLoadMore))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsSearching { get; private set; }

    /// <summary>A later read is on its way: what shows stays, and the grid's end waits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLoadMore))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoadingMore { get; private set; }

    /// <summary>TMDB has another page to read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLoadMore), nameof(IsEmptySoFar))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool CanLoadMore { get; private set; }

    /// <summary>The last later read failed: Try again reads it again; the grid's end no longer does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLoadMore))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMoreError { get; private set; }

    /// <summary>Load more, for the keyboard, while TMDB has more and nothing failed or is on its way.</summary>
    public bool ShowsLoadMore => CanLoadMore && !IsSearching && !IsLoadingMore && !HasMoreError;

    /// <summary>The pages read so far held only titles the library has, and TMDB has more: Load more looks further.</summary>
    public bool IsEmptySoFar => IsShown && Cards.Count == 0 && CanLoadMore;

    /// <summary>The library search found nothing for the text, which <see cref="Note"/> then says.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note))]
    public partial bool LibraryFoundNothing { get; set; }

    /// <summary>"Nothing in your library matches “heat”." under the heading, or empty.</summary>
    public string Note => LibraryFoundNothing && Query.Length > 0 ? $"Nothing in your library matches “{Query}”." : "";

    /// <summary>What Search's backdrop shows of <paramref name="card"/>, one of the cards shown; null for another.</summary>
    internal BrowseHeroTitle? BackdropOf(MediaCard card) => _backdrops.GetValueOrDefault(card.Id);

    /// <summary>The read in flight, for tests.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Reads what TMDB finds for <paramref name="query"/>, dropping what was shown for the last text.</summary>
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
        _run = new CancellationTokenSource();
        return Loading = ReadAsync(1, first: true, _run.Token);
    }

    /// <summary>No text, or another session: nothing shown, nothing in flight.</summary>
    public void Clear()
    {
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
        _reading = false;
        RequestNotice.Clear();
        Query = "";
        LibraryFoundNothing = false;
        IsSearching = false;
        Hide();
    }

    /// <summary>Reads on, when the grid's end comes into view or Load more is pressed.</summary>
    [RelayCommand(CanExecute = nameof(ShowsLoadMore))]
    private Task LoadMoreAsync() =>
        ShowsLoadMore && !_reading && _nextPage is int page && _run is { } run ? Loading = ReadAsync(page, first: false, run.Token) : Task.CompletedTask;

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

    // One read: TMDB's pages from page on, until a screenful came, PagesPerRead were read, or TMDB has no more.
    private async Task ReadAsync(int page, bool first, CancellationToken cancellationToken)
    {
        _reading = true;
        IsLoadingMore = !first;
        try
        {
            if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
            {
                return;
            }
            if (first)
            {
                // Read once per profile; a read in flight, the sidebar's, is waited for.
                await _feature.RefreshAsync().WaitAsync(cancellationToken);
                if (!_feature.IsEnabled)
                {
                    return;
                }
            }
            int added = 0;
            for (int read = 1; ; read++)
            {
                var found = await _requests.SearchAsync(server.BaseUri, Query, _mediaType, page, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                added += Add(found.Results);
                int last = Math.Clamp(found.TotalPages, 0, RequestsApi.TmdbMaxPage);
                _nextPage = page < last ? page + 1 : null;
                CanLoadMore = _nextPage is not null;
                if (_nextPage is not int next || added >= Fill || read >= PagesPerRead)
                {
                    break;
                }
                page = next;
            }
            // Nothing to add on TMDB's only pages leaves the section out; more pages to look through keep it.
            IsShown = Cards.Count > 0 || CanLoadMore;
            OnPropertyChanged(nameof(IsEmptySoFar));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogWarning("Searching titles to request failed: {Failure}", ApiFailureText.Describe(ex));
            // A first read that failed before anything came leaves the section out; otherwise what came stays, and Try
            // again reads the page that failed.
            if (first && Cards.Count == 0)
            {
                Hide();
            }
            else
            {
                IsShown = true;
                HasMoreError = true;
                OnPropertyChanged(nameof(IsEmptySoFar));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer text replaced this read.
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _reading = false;
                IsSearching = false;
                IsLoadingMore = false;
            }
        }
    }

    // The titles the library does not have, each once across the pages read.
    private int Add(IEnumerable<RequestMediaResult> results)
    {
        int added = 0;
        foreach (var result in results.Where(r => r.Availability != "available" && r.TmdbId > 0))
        {
            var card = RequestCards.Of(result);
            if (!_destinations.ContainsKey(card.Id) && RequestCards.Destination(result) is { } destination)
            {
                _destinations[card.Id] = destination;
                _backdrops[card.Id] = BrowseHeroTitle.Of(result);
                Cards.Add(card);
                added++;
            }
        }
        return added;
    }

    private void Hide()
    {
        IsShown = false;
        _destinations.Clear();
        _backdrops.Clear();
        Cards.Clear();
        _nextPage = null;
        CanLoadMore = false;
        HasMoreError = false;
        IsLoadingMore = false;
        OnPropertyChanged(nameof(IsEmptySoFar));
    }
}
