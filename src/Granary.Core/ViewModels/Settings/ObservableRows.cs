using System.Collections.ObjectModel;

namespace Granary.Core.ViewModels.Settings;

/// <summary>An editor's rows brought to a new order by moving them rather than making the list anew, so a row keeps its controls as it goes.</summary>
internal static class ObservableRows
{
    public static void Sync<T>(ObservableCollection<T> shown, IReadOnlyList<T> rows)
    {
        for (int index = shown.Count - 1; index >= 0; index--)
        {
            if (!rows.Contains(shown[index]))
            {
                shown.RemoveAt(index);
            }
        }
        for (int index = 0; index < rows.Count; index++)
        {
            int at = shown.IndexOf(rows[index]);
            if (at < 0)
            {
                shown.Insert(index, rows[index]);
            }
            else if (at != index)
            {
                shown.Move(at, index);
            }
        }
    }
}
