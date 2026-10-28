using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Catalog;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>A content rating the filter can match, switched on or off in the flyout.</summary>
public sealed partial class LibraryFilterToggle(string value) : ObservableObject
{
    public string Value { get; } = value;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// The Filters flyout of a library's grid: one genre, any of the content ratings, and a year range, the
/// filters <c>GET /api/v2/catalog</c> takes (docs/api-contract.md, 7.4). The flyout edits a draft;
/// <see cref="ApplyCommand"/> and <see cref="ClearCommand"/> commit it and restart the grid.
/// </summary>
public sealed partial class LibraryFiltersViewModel(Func<Task> applied, ILogger logger) : ObservableObject
{
    /// <summary>The first genre choice, which filters nothing.</summary>
    public const string AnyGenre = "Any genre";

    public const int MinimumYear = 1;
    public const int MaximumYear = 9999;

    /// <summary>The applied genre, or null.</summary>
    public string? Genre { get; private set; }

    /// <summary>The applied ratings; a title matches any of them.</summary>
    public IReadOnlyList<string> ContentRatings { get; private set; } = [];

    public int? YearFrom { get; private set; }

    public int? YearTo { get; private set; }

    /// <summary>The count on the Filters pill: the genre, each rating, and the year range.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial int ActiveCount { get; private set; }

    public bool HasActiveFilters => ActiveCount > 0;

    /// <summary><see cref="AnyGenre"/> and the library's genres, for a list picked by index.</summary>
    public ObservableCollection<string> GenreChoices { get; } = [AnyGenre];

    public ObservableCollection<LibraryFilterToggle> RatingOptions { get; } = [];

    [ObservableProperty]
    public partial int DraftGenreIndex { get; set; }

    /// <summary>NaN while empty, as a number box reports it.</summary>
    [ObservableProperty]
    public partial double DraftYearFrom { get; set; } = double.NaN;

    [ObservableProperty]
    public partial double DraftYearTo { get; set; } = double.NaN;

    /// <summary>The genre and rating lists could not be read; the year range still works.</summary>
    [ObservableProperty]
    public partial bool HasOptionsError { get; private set; }

    public bool HasRatings => RatingOptions.Count > 0;

    /// <summary>The query with the applied filters.</summary>
    public CatalogQuery ApplyTo(CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query with
        {
            Genre = Genre,
            ContentRatings = ContentRatings.Count > 0 ? ContentRatings : null,
            YearMin = YearFrom,
            YearMax = YearTo,
        };
    }

    /// <summary>Reads the facet values of <paramref name="scope"/> for the genre and rating lists.</summary>
    public async Task LoadOptionsAsync(CatalogApi catalog, Uri server, CatalogQuery scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            var filters = await catalog.GetFiltersAsync(server, scope, cancellationToken: cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            HasOptionsError = false;
            ShowOptions(filters);
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Another library took over.
                return;
            }
            logger.LogWarning(ex, "Could not load the filter options of a library");
            HasOptionsError = true;
        }
    }

    /// <summary>Forgets the applied filters and the options, for another library.</summary>
    public void Reset()
    {
        Commit(null, [], null, null);
        HasOptionsError = false;
        ShowOptions(new CatalogFilters());
    }

    /// <summary>
    /// Applies remembered filters without restarting anything; the options keep showing what they have. Years are taken
    /// as Apply takes them, so a damaged file can't make a query the server refuses.
    /// </summary>
    public void Restore(string? genre, IReadOnlyList<string> ratings, int? from, int? to)
    {
        ArgumentNullException.ThrowIfNull(ratings);
        int? first = Year(from);
        int? last = Year(to);
        if (first > last)
        {
            (first, last) = (last, first);
        }
        Commit(string.IsNullOrWhiteSpace(genre) ? null : genre, [.. ratings.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal)],
            first, last);
        BeginEdit();
    }

    /// <summary>Fills the draft from the applied filters when the flyout opens.</summary>
    public void BeginEdit()
    {
        DraftGenreIndex = Genre is null ? 0 : IndexOfGenre(Genre);
        foreach (var option in RatingOptions)
        {
            option.IsSelected = ContentRatings.Contains(option.Value, StringComparer.Ordinal);
        }
        DraftYearFrom = YearFrom ?? double.NaN;
        DraftYearTo = YearTo ?? double.NaN;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ApplyAsync()
    {
        string? genre = DraftGenreIndex > 0 && DraftGenreIndex < GenreChoices.Count ? GenreChoices[DraftGenreIndex] : null;
        string[] ratings = [.. RatingOptions.Where(o => o.IsSelected).Select(o => o.Value)];
        int? from = Year(DraftYearFrom);
        int? to = Year(DraftYearTo);
        if (from > to)
        {
            (from, to) = (to, from);
        }
        Commit(genre, ratings, from, to);
        return applied();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ClearAsync()
    {
        Commit(null, [], null, null);
        BeginEdit();
        return applied();
    }

    /// <summary>Clears the filters without restarting the grid; the caller restarts it.</summary>
    internal void ClearSilently()
    {
        Commit(null, [], null, null);
        BeginEdit();
    }

    private void Commit(string? genre, IReadOnlyList<string> ratings, int? from, int? to)
    {
        Genre = genre;
        ContentRatings = ratings;
        YearFrom = from;
        YearTo = to;
        ActiveCount = (genre is null ? 0 : 1) + ratings.Count + (from is null && to is null ? 0 : 1);
    }

    private void ShowOptions(CatalogFilters filters)
    {
        GenreChoices.Clear();
        GenreChoices.Add(AnyGenre);
        foreach (string genre in filters.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.Ordinal))
        {
            GenreChoices.Add(genre);
        }
        RatingOptions.Clear();
        foreach (string rating in filters.ContentRatings.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal))
        {
            RatingOptions.Add(new LibraryFilterToggle(rating) { IsSelected = ContentRatings.Contains(rating, StringComparer.Ordinal) });
        }
        OnPropertyChanged(nameof(HasRatings));
        DraftGenreIndex = Genre is null ? 0 : IndexOfGenre(Genre);
    }

    private int IndexOfGenre(string genre)
    {
        int index = GenreChoices.IndexOf(genre);
        return index > 0 ? index : 0;
    }

    private static int? Year(double value) =>
        double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), MinimumYear, MaximumYear) : null;

    // A remembered year: none when it isn't a year at all, else within the years a query takes.
    private static int? Year(int? value) => value is > 0 ? Math.Clamp(value.Value, MinimumYear, MaximumYear) : null;
}
