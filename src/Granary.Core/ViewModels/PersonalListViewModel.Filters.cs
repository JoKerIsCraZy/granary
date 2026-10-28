using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;

namespace Granary.Core.ViewModels;

// The list's Filters, as the web's catalog page offers them over Favorites, Watchlist, History and a collection
// (CatalogFiltersPanel): the library page's genre, content ratings and year range, with their options read for the list.
public sealed partial class PersonalListViewModel
{
    private CancellationTokenSource _filterOptions = new();

    /// <summary>The Filters flyout: genre, content ratings and years, applied to the list.</summary>
    public LibraryFiltersViewModel Filters { get; }

    /// <summary>Filters narrow the list; an empty result then offers to clear them.</summary>
    public bool IsNarrowed => Filters.HasActiveFilters;

    /// <summary>Clears the filters, from the empty state.</summary>
    [RelayCommand]
    private Task ClearFiltersAsync()
    {
        Filters.ClearSilently();
        return StartAsync();
    }

    // Another list: its filters start empty, and their options are the ones of its titles.
    private void ResetFilters()
    {
        var previous = _filterOptions;
        _filterOptions = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        Filters.Reset();
        if (_session is { State: SessionState.Ready, ActiveServer: { } server } && Source.Length > 0)
        {
            _ = Filters.LoadOptionsAsync(_catalog, server.BaseUri, new CatalogQuery
            {
                Source = Source,
                CollectionId = _collection?.CollectionId,
                LibraryId = _collection?.LibraryId,
            }, _filterOptions.Token);
        }
    }

    private void OnFiltersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryFiltersViewModel.ActiveCount))
        {
            OnPropertyChanged(nameof(IsNarrowed));
        }
    }
}
