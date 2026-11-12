namespace Granary.Core.Texts;

/// <summary>
/// Names in a sentence, "Movies, Kids and 4K Movies", as the web's <c>joinNames</c> writes them with
/// <c>Intl.ListFormat</c> (a long "and" list; English without the serial comma, as en-GB has it). .NET has no list
/// format, so the patterns are Granary's texts, built as CLDR builds a list: the last two by the end pattern, every name
/// before them by the start pattern, two names alone by their own.
/// </summary>
public static class TextList
{
    /// <summary>"Maya", "Maya and Leo", "Maya, Leo and Sam"; empty for none.</summary>
    public static string And(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        switch (names.Count)
        {
            case 0:
                return "";
            case 1:
                return names[0];
            case 2:
                return AppText.Get("granary.list.andTwo", ("first", names[0]), ("second", names[1]));
        }
        string list = AppText.Get("granary.list.andEnd", ("start", names[^2]), ("last", names[^1]));
        for (int i = names.Count - 3; i >= 0; i--)
        {
            list = AppText.Get("granary.list.andStart", ("first", names[i]), ("rest", list));
        }
        return list;
    }
}
