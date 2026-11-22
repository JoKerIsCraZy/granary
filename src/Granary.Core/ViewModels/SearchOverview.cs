namespace Granary.Core.ViewModels;

/// <summary>
/// What Search shows, as the web's <c>pages/searchResultsView.ts</c>: the overview (a few rows of each kind of result, so
/// the titles to request stay near the top whatever the library returns), or the whole list of one kind.
/// </summary>
public enum SearchView
{
    Overview,
    Library,
    Requests,
}

/// <summary>The overview's cut of the library's first page, as the web's <c>splitOverviewResults</c>.</summary>
public static class SearchOverview
{
    /// <summary>The rows of best matches above the titles to request.</summary>
    public const int BestMatchRows = 2;

    /// <summary>The rows of more results under people; anything past them is in the library view.</summary>
    public const int MoreResultRows = 3;

    /// <summary>
    /// Two rows of <paramref name="items"/> at <paramref name="columns"/> a row, then up to three more, so the overview stays
    /// as tall whether the search finds twenty titles or two thousand. <c>HasMoreThanShown</c> when the server has another
    /// page or the page holds more than the rows show.
    /// </summary>
    public static (IReadOnlyList<T> Best, IReadOnlyList<T> More, bool HasMoreThanShown) Split<T>(IReadOnlyList<T> items, int columns, bool serverHasMore)
    {
        ArgumentNullException.ThrowIfNull(items);
        int perRow = Math.Max(1, columns);
        int bestEnd = Math.Min(items.Count, perRow * BestMatchRows);
        int moreEnd = Math.Min(items.Count, bestEnd + perRow * MoreResultRows);
        return ([.. items.Take(bestEnd)], [.. items.Skip(bestEnd).Take(moreEnd - bestEnd)], serverHasMore || items.Count > moreEnd);
    }
}
