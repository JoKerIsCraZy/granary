using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels;

// The overview and the two lists of their own, as the web's pages/SearchResults.tsx: two rows of the library's titles, the
// titles to request ("Not in your library"), people and three more rows; "In your library" and "Request to add" show the
// whole lists. Without library titles (none, or the search failed) the titles to request are the answer, in their grid.
public sealed partial class SearchViewModel
{
    /// <summary>The titles to request the overview's row shows, as the web's twenty.</summary>
    public const int RequestRowLimit = 20;

    // The library's first page and whether the server has more, as the search found them; the overview cuts this.
    private (IReadOnlyList<MediaCard> Cards, bool HasMore) _firstPage = ([], false);

    /// <summary>The view picked: the overview, or a whole list. <see cref="ShownView"/> is what shows.</summary>
    [ObservableProperty]
    public partial SearchView View { get; private set; }

    /// <summary>The columns the overview's grid has at its width, told by the page; the rows are cut by it.</summary>
    [ObservableProperty]
    public partial int Columns { get; set; }

    /// <summary>The first two rows of the library's titles.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MediaCard> BestCards { get; private set; } = [];

    /// <summary>Up to three more rows, under people.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MediaCard> MoreCards { get; private set; } = [];

    /// <summary>The library has more than the overview's rows show: "Show all results" opens the library view.</summary>
    [ObservableProperty]
    public partial bool ShowsAllResults { get; private set; }

    /// <summary>The overview's row of titles to request, at most <see cref="RequestRowLimit"/>.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MediaCard> RequestRow { get; private set; } = [];

    /// <summary>
    /// What shows: a request view falls back to the overview once TMDB has nothing to add for the text (a text of one
    /// character, a server that takes no requests, or a search with no titles to add), as the web's.
    /// </summary>
    public SearchView ShownView => View == SearchView.Requests && !Requests.IsSearching && !Requests.IsShown ? SearchView.Overview : View;

    public bool IsOverviewShown => ShownView == SearchView.Overview;

    public bool IsLibraryViewShown => ShownView == SearchView.Library;

    /// <summary>The overview's library rows: titles found, and the search did not fail.</summary>
    public bool ShowsBestCards => IsOverviewShown && BestCards.Count > 0 && !HasError;

    public bool ShowsMoreCards => IsOverviewShown && MoreCards.Count > 0 && !HasError;

    /// <summary>"More results" over the more rows under Relevance; under another sort they go on with the first rows' list.</summary>
    public bool ShowsMoreHeading => ShowsMoreCards && !IsSortedOtherwise;

    /// <summary>"Show all results" beside the first rows' heading, under another sort.</summary>
    public bool ShowsAllLinkAtFirstRows => ShowsAllResults && IsSortedOtherwise && ShowsBestCards;

    /// <summary>"Show all results" beside "More results", under Relevance.</summary>
    public bool ShowsAllLinkAtMoreRows => ShowsAllResults && ShowsMoreHeading;

    /// <summary>The "Show all results" button under the overview's rows.</summary>
    public bool ShowsAllButton => ShowsAllResults && IsOverviewShown && !HasError;

    /// <summary>The overview's row of titles to request, between the library's rows, while the library found titles.</summary>
    public bool ShowsRequestRow => IsOverviewShown && !_libraryEmpty && !HasError && _firstPage.Cards.Count > 0 && RequestRow.Count > 0;

    /// <summary>
    /// The grid of every title to request: the request view, or the overview while the library found nothing or failed, where
    /// it is the answer.
    /// </summary>
    public bool ShowsRequestGrid => Requests.IsShown
        && (ShownView == SearchView.Requests || (IsOverviewShown && (_libraryEmpty || (HasError && _firstPage.Cards.Count == 0))));

    /// <summary>People after the titles to request under Relevance, as the web's.</summary>
    public bool ShowsPeopleAfterRequests => ShowsPeople && !IsSortedOtherwise;

    /// <summary>People after the more rows under another sort, so the sorted list reads on unbroken.</summary>
    public bool ShowsPeopleAfterMore => ShowsPeople && IsSortedOtherwise;

    /// <summary>"Request to add" among the views: TMDB found titles to add, or has more pages to look through.</summary>
    public bool ShowsRequestsTab => Requests.IsShown;

    /// <summary>"No items found.", a failure or what is loading, except in the request view, which has its own.</summary>
    public bool ShowsStatus => StatusText.Length > 0 && ShownView != SearchView.Requests;

    /// <summary>The library view's count: the titles read so far, "60+" while the server has more, as the web's.</summary>
    public string LibraryCountText => ResultsQuery.Length == 0 || _isSearchingTitles || HasError
        ? ""
        : CountText(Cards.Count, _titles?.HasMore == true);

    /// <summary>The request view's count: the row's titles, "20+" while there are more.</summary>
    public string RequestCountText => RequestRow.Count == 0
        ? ""
        : CountText(RequestRow.Count, Requests.CanLoadMore || Requests.Cards.Count > RequestRow.Count);

    /// <summary>Opens a view; the page goes back to its top, as the web scrolls to it.</summary>
    [RelayCommand]
    private void ShowView(SearchView view)
    {
        View = view;
        ShowViews();
    }

    /// <summary>"Show all results": the library view.</summary>
    [RelayCommand]
    private void ShowLibrary() => ShowView(SearchView.Library);

    /// <summary>"Explore all" on the row of titles to request: the request view.</summary>
    [RelayCommand]
    private void ShowRequests() => ShowView(SearchView.Requests);

    public bool IsRequestsViewShown => ShownView == SearchView.Requests;

    /// <summary>The overview grid's columns, from the grid as its width changes.</summary>
    [RelayCommand]
    private void SetColumns(int columns) => Columns = columns;

    partial void OnColumnsChanged(int value) => ShowOverview();

    partial void OnViewChanged(SearchView value) => ShowViews();

    // The overview's rows, cut from the first page at the grid's columns.
    private void ShowOverview()
    {
        var (best, more, hasMore) = SearchOverview.Split(_firstPage.Cards, Columns, _firstPage.HasMore);
        if (!best.SequenceEqual(BestCards))
        {
            BestCards = best;
        }
        if (!more.SequenceEqual(MoreCards))
        {
            MoreCards = more;
        }
        ShowsAllResults = hasMore;
        ShowViews();
    }

    private void ShowRequestRow()
    {
        var row = Requests.Cards.Take(RequestRowLimit).ToList();
        if (!row.SequenceEqual(RequestRow))
        {
            RequestRow = row;
        }
        ShowViews();
    }

    // Everything that follows the view, the results and the sort.
    private void ShowViews()
    {
        foreach (var name in ViewProperties)
        {
            OnPropertyChanged(name);
        }
    }

    private static readonly string[] ViewProperties =
    [
        nameof(ShownView), nameof(IsOverviewShown), nameof(IsLibraryViewShown), nameof(IsRequestsViewShown), nameof(ShowsBoxSets), nameof(ShowsPeople),
        nameof(ShowsBestCards), nameof(ShowsMoreCards), nameof(ShowsMoreHeading), nameof(ShowsAllLinkAtFirstRows),
        nameof(ShowsAllLinkAtMoreRows), nameof(ShowsAllButton), nameof(ShowsRequestRow), nameof(ShowsRequestGrid),
        nameof(ShowsPeopleAfterRequests), nameof(ShowsPeopleAfterMore), nameof(ShowsRequestsTab), nameof(ShowsStatus),
        nameof(LibraryCountText), nameof(RequestCountText), nameof(FirstRowsHeading), nameof(SortedByText),
    ];

    // Meilisearch counts no exact total: past what was read it is "60+".
    private static string CountText(int count, bool more) => more
        ? AppText.Get("web.search.results.atLeastCount", ("count", AppText.Number(count)))
        : AppText.Number(count);
}
