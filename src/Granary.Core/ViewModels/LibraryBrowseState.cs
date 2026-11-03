namespace Granary.Core.ViewModels;

/// <summary>
/// How a library's titles were last sorted and filtered, kept per server, profile and library so the library opens that
/// way again, as the Apple app's BrowsePrefsStore keeps them. A letter jumped to is not part of it.
/// </summary>
/// <param name="SortField">A <see cref="LibrarySortOption.Field"/>.</param>
public sealed record LibraryBrowseState(string SortField, bool Descending, LibraryFilterSet Filters)
{
    /// <summary>Title, A to Z, unfiltered: how a library opens the first time.</summary>
    public static LibraryBrowseState Default { get; } = new(LibrarySortOption.Title.Field, false, LibraryFilterSet.None);
}
