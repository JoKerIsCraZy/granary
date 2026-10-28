namespace Granary.Core.ViewModels;

/// <summary>
/// How a library's titles were last sorted and filtered, kept per server, profile and library so the library opens that
/// way again, as the Apple app's BrowsePrefsStore keeps them. A letter jumped to is not part of it.
/// </summary>
/// <param name="SortField">A <see cref="LibrarySortOption.Field"/>.</param>
public sealed record LibraryBrowseState(string SortField, bool Descending, string? Genre, IReadOnlyList<string> ContentRatings, int? YearFrom, int? YearTo)
{
    /// <summary>Title, A to Z, unfiltered: how a library opens the first time.</summary>
    public static LibraryBrowseState Default { get; } = new(LibrarySortOption.Title.Field, false, null, [], null, null);

    public bool Equals(LibraryBrowseState? other) => other is not null && SortField == other.SortField && Descending == other.Descending
        && Genre == other.Genre && ContentRatings.SequenceEqual(other.ContentRatings) && YearFrom == other.YearFrom && YearTo == other.YearTo;

    public override int GetHashCode() => HashCode.Combine(SortField, Descending, Genre, ContentRatings.Count, YearFrom, YearTo);
}
