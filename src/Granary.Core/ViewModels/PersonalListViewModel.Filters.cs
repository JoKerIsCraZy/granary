using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Auth;

namespace Granary.Core.ViewModels;

// The list's Filters, as the web's catalog page offers them over Favorites, Watchlist, History and a collection
// (CatalogFiltersPanel): the library page's genre, content ratings and year range, with their options read for the list
// and the kind of titles it shows.
public sealed partial class PersonalListViewModel
{
    private CancellationTokenSource _filterOptions = new();
    // The list, profile and kind of titles the options were last asked for.
    private (string? ServerId, string? ProfileId, string Source, CollectionRouteParameter? Collection, string? Type)? _optionsFor;

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

    // Another list, profile or server: its filters start empty, and their options are the ones of its titles, asked for
    // when the list loads (EnsureFilterOptions).
    private void ResetFilters()
    {
        Filters.Reset();
        _optionsFor = null;
    }

    // Asks for the options unless they were asked for this list and kind of titles already; a read that failed, or one
    // the session wasn't ready for, is asked again.
    private void EnsureFilterOptions()
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server } || Source.Length == 0)
        {
            return;
        }
        var key = (server.Id, _session.ProfileId, Source, _collection, Scope.Type);
        if (_optionsFor == key && !Filters.HasOptionsError)
        {
            return;
        }
        _optionsFor = key;
        var previous = _filterOptions;
        _filterOptions = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        _ = Filters.LoadOptionsAsync(_catalog, server.BaseUri, new CatalogQuery
        {
            Source = Source,
            CollectionId = _collection?.CollectionId,
            LibraryId = _collection?.LibraryId,
            Type = Scope.Type,
        }, _filterOptions.Token);
    }

    private void OnFiltersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryFiltersViewModel.ActiveCount))
        {
            OnPropertyChanged(nameof(IsNarrowed));
        }
    }
}
