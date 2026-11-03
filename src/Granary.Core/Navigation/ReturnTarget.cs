namespace Granary.Core.Navigation;

/// <summary>
/// The card focus returns to when Back comes to a list, as Silo's TV app (<c>TvReturnTarget</c>): the title that had
/// focus, wherever it is now, else the same place in the list, within its end.
/// </summary>
public static class ReturnTarget
{
    /// <summary>The index to focus; -1 for an empty list.</summary>
    public static int IndexFor<T>(IReadOnlyList<T> items, T item, int previousIndex) =>
        IndexFor(items, item, previousIndex, static each => each);

    /// <summary>The index to focus, titles matched by <paramref name="key"/>; -1 for an empty list.</summary>
    public static int IndexFor<T, TKey>(IReadOnlyList<T> items, T item, int previousIndex, Func<T, TKey> key)
    {
        if (items.Count == 0)
        {
            return -1;
        }
        var wanted = key(item);
        for (int i = 0; i < items.Count; i++)
        {
            if (EqualityComparer<TKey>.Default.Equals(key(items[i]), wanted))
            {
                return i;
            }
        }
        return Math.Clamp(previousIndex, 0, items.Count - 1);
    }
}
