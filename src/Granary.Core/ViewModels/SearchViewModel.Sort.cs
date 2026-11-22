using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;
using Granary.Core.Texts;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// Search's sort and filters, as the web's search toolbar (CatalogFiltersPanel with relevanceSort and the personal filters
// and sorts): Relevance leads the sort menu and is a search's own order, with no ascending or descending; the library's
// sorts follow, the personal ones too. Under another sort the overview reads as one sorted list.
public sealed partial class SearchViewModel
{
    private static readonly IReadOnlySet<string> NoRatingSources = new HashSet<string>();

    private IReadOnlySet<string> _shownRatings = NoRatingSources;
    // The session the rating sources were asked for.
    private (string? ServerId, string? ProfileId)? _ratingsFor;
    private CancellationTokenSource _filterOptions = new();
    // The session and type the filter options were last asked for.
    private (string? ServerId, string? ProfileId, SearchScope Scope)? _optionsFor;

    /// <summary>The Filters flyout, applied to the search; Watch Status among them, as the web offers personal filters.</summary>
    public LibraryFiltersViewModel Filters { get; }

    /// <summary>The sorts under Relevance, for the scope and the rating sources the server shows.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LibrarySortOption> SortOptions { get; private set; } = LibrarySortOption.ForSearch(MediaScopes.Video, NoRatingSources);

    /// <summary>The sort picked; null is Relevance, the server's order for a text, which sends no sort.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel), nameof(IsOrderShown), nameof(IsSortedOtherwise))]
    public partial LibrarySortOption? Sort { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrderLabel))]
    public partial bool IsDescending { get; private set; }

    /// <summary>"Relevance", or the sort's name.</summary>
    public string SortLabel => Sort?.Label ?? AppText.Get("web.catalog.sort.relevance");

    public string OrderLabel => IsDescending ? AppText.Get("web.catalog.sort.descending") : AppText.Get("web.catalog.sort.ascending");

    /// <summary>Relevance has no order of its own.</summary>
    public bool IsOrderShown => Sort is not null;

    /// <summary>Another sort than Relevance: "Best matches" means nothing then.</summary>
    public bool IsSortedOtherwise => Sort is not null;

    /// <summary>"Best matches" under Relevance, "In your library" under another sort.</summary>
    public string FirstRowsHeading => AppText.Get(IsSortedOtherwise ? "web.search.results.inLibrary" : "web.search.results.bestMatches");

    /// <summary>"Sorted by Year" under the first rows' heading, beside Back to relevance.</summary>
    public string SortedByText => Sort is { } sort ? AppText.Get("web.search.results.sortedBy", ("sort", sort.Label)) : "";

    /// <summary>Sorts by <paramref name="option"/>, in the order it starts in; null is Relevance.</summary>
    [RelayCommand]
    private Task SetSortAsync(LibrarySortOption? option)
    {
        if (option == Sort)
        {
            return Task.CompletedTask;
        }
        Sort = option;
        IsDescending = option?.StartsDescending ?? false;
        OfferSorts();
        return SearchAgainAsync();
    }

    [RelayCommand]
    private Task BackToRelevanceAsync() => SetSortAsync(null);

    [RelayCommand]
    private Task SortAscendingAsync() => SetOrderAsync(descending: false);

    [RelayCommand]
    private Task SortDescendingAsync() => SetOrderAsync(descending: true);

    private Task SetOrderAsync(bool descending)
    {
        if (Sort is null || descending == IsDescending)
        {
            return Task.CompletedTask;
        }
        IsDescending = descending;
        return SearchAgainAsync();
    }

    // Another sort or filters: the library is asked again at once, in the view shown; the titles to request stay.
    private Task SearchAgainAsync()
    {
        ShowViews();
        if (Query.Trim().Length == 0)
        {
            return Task.CompletedTask;
        }
        Schedule(TimeSpan.Zero, keepRequests: true);
        return Searching;
    }

    // The sorts of the scope, keeping the picked one even when its rating is hidden now.
    private void OfferSorts()
    {
        var offered = LibrarySortOption.ForSearch(TypeOf(Scope), _shownRatings, keep: Sort?.Field);
        if (!offered.SequenceEqual(SortOptions))
        {
            SortOptions = offered;
        }
    }

    // The rating sources once per session, and the filter options once per session and scope; a failed read is asked again.
    private void PrepareSortsAndFilters()
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            return;
        }
        if (_ratingsFor != (server.Id, _session.ProfileId))
        {
            _ratingsFor = (server.Id, _session.ProfileId);
            _ = LoadRatingSourcesAsync(server.BaseUri, _filterOptions.Token);
        }
        var key = (server.Id, _session.ProfileId, Scope);
        if (_optionsFor == key && !Filters.HasOptionsError)
        {
            return;
        }
        _optionsFor = key;
        var previous = _filterOptions;
        _filterOptions = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        _ = Filters.LoadOptionsAsync(_catalog, server.BaseUri, new CatalogQuery { Type = TypeOf(Scope) }, _filterOptions.Token);
    }

    // A server without ratings (404) shows none; one that can't be asked leaves the sorts as they were.
    private async Task LoadRatingSourcesAsync(Uri server, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> shown;
        try
        {
            shown = (await _catalog.GetRatingsCapabilityAsync(server, cancellationToken)).Shown;
        }
        catch (SiloApiException ex) when (ex.Status == 404)
        {
            shown = NoRatingSources;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("The server's rating sources are unknown: {Failure}", ApiFailureText.Describe(ex));
                _ratingsFor = null;
            }
            return;
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            _shownRatings = shown;
            OfferSorts();
        }
    }

    // Another server or profile: Relevance again, no filters, and their options asked for anew.
    private void ForgetSortsAndFilters()
    {
        _filterOptions.Cancel();
        _filterOptions.Dispose();
        _filterOptions = new CancellationTokenSource();
        _ratingsFor = null;
        _optionsFor = null;
        _shownRatings = NoRatingSources;
        Sort = null;
        IsDescending = false;
        Filters.Reset();
        OfferSorts();
    }
}
