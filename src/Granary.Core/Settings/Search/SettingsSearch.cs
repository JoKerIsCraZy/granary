using System.Globalization;
using System.Text;

namespace Granary.Core.Settings.Search;

/// <summary>What a found setting is on its page, so the page can find it again and show it.</summary>
public enum SettingsEntryKind
{
    /// <summary>A section (SettingsGroup), by its title.</summary>
    Section,

    /// <summary>A setting's row (SettingsRow), by its label.</summary>
    Row,

    /// <summary>A heading of the page's own, by its text.</summary>
    Heading,

    /// <summary>A control named for screen readers, by its name.</summary>
    Control,
}

/// <summary>
/// One thing a settings page shows that search finds, in the language on screen. Keywords are extra terms that are never
/// shown: the English text in another language, and English words kept next to the setting.
/// </summary>
public sealed record SettingsSearchEntry(SettingsEntryKind Kind, string Label, string? Description, IReadOnlyList<string> Keywords);

/// <summary>A settings page as its sidebar entry names it, with what the page shows.</summary>
public sealed record SettingsSearchPage(
    string Tag, string Label, string? Description, IReadOnlyList<string> Keywords, IReadOnlyList<SettingsSearchEntry> Settings);

/// <summary>A group of the settings' sidebar, by its heading.</summary>
public sealed record SettingsSearchGroup(string Id, string Label, IReadOnlyList<string> Keywords, IReadOnlyList<SettingsSearchPage> Pages);

/// <summary>
/// The settings' search, as the web's (web/src/components/settings/settingsSearch.ts): every word of the query has to
/// start a word of the text, or sit inside one once it has four letters. Case, accents and punctuation do not count;
/// letters and digits of any script do, as the texts are in the language on screen. A group whose own name matches keeps
/// all its pages.
/// </summary>
public static class SettingsSearch
{
    private const int InsideWordLength = 4;

    /// <summary>The groups with the pages that match; every page when the query has no words.</summary>
    public static IReadOnlyList<SettingsSearchGroup> FilterGroups(IReadOnlyList<SettingsSearchGroup> groups, string query)
    {
        ArgumentNullException.ThrowIfNull(groups);
        string[] tokens = Tokens(query);
        if (tokens.Length == 0)
        {
            return groups;
        }
        var found = new List<SettingsSearchGroup>();
        foreach (var group in groups)
        {
            bool groupMatches = Matches(Normalize(Join([group.Label, .. group.Keywords])), tokens);
            var pages = groupMatches ? group.Pages : [.. group.Pages.Where(page => Matches(PageText(group, page), tokens))];
            if (pages.Count > 0)
            {
                found.Add(group with { Pages = pages });
            }
        }
        return found;
    }

    /// <summary>The settings that match one by one, in the page's order; none when the query has no words.</summary>
    public static IReadOnlyList<SettingsSearchEntry> FilterEntries(IReadOnlyList<SettingsSearchEntry> entries, string query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string[] tokens = Tokens(query);
        return tokens.Length == 0 ? [] : [.. entries.Where(entry => Matches(EntryText(entry), tokens))];
    }

    /// <summary>The pages the groups list: what the count under the search box counts.</summary>
    public static int CountPages(IReadOnlyList<SettingsSearchGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return groups.Sum(group => group.Pages.Count);
    }

    // Lower case, accents taken off, and every run of what is not a letter, digit or mark of any script made one space.
    internal static string Normalize(string text)
    {
        string lower = text.ToLowerInvariant();
        string decomposed;
        try
        {
            decomposed = lower.Normalize(NormalizationForm.FormKD);
        }
        catch (ArgumentException)
        {
            // A lone half of a surrogate pair, as an input method leaves it while it composes an emoji, is no character:
            // it becomes U+FFFD, which is no letter either.
            decomposed = Whole(lower).Normalize(NormalizationForm.FormKD);
        }
        var normalized = new StringBuilder(decomposed.Length);
        bool gap = false;
        foreach (var rune in decomposed.EnumerateRunes())
        {
            // The combining accents, as the web's [̀-ͯ]; other marks belong to their letters.
            if (rune.Value is >= 0x300 and <= 0x36F)
            {
                continue;
            }
            if (!IsWordPart(Rune.GetUnicodeCategory(rune)))
            {
                gap = true;
                continue;
            }
            if (gap && normalized.Length > 0)
            {
                normalized.Append(' ');
            }
            gap = false;
            normalized.Append(rune.ToString());
        }
        return normalized.ToString();
    }

    // The text with each lone half of a surrogate pair as U+FFFD, as reading it rune by rune gives it.
    private static string Whole(string text)
    {
        var whole = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            whole.Append(rune.ToString());
        }
        return whole.ToString();
    }

    private static bool IsWordPart(UnicodeCategory category) => category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
        or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
        or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static string[] Tokens(string? query) => Normalize(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool Matches(string text, string[] tokens)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(token => words.Any(word => word.StartsWith(token, StringComparison.Ordinal)
            || (token.Length >= InsideWordLength && word.Contains(token, StringComparison.Ordinal))));
    }

    // A page's text: its group's, its own and all its settings', so the words of a query may come from different ones.
    private static string PageText(SettingsSearchGroup group, SettingsSearchPage page) => Normalize(Join(
    [
        group.Label, .. group.Keywords, page.Label, page.Description, .. page.Keywords,
        .. page.Settings.SelectMany(setting => (IEnumerable<string?>)[setting.Label, setting.Description, .. setting.Keywords]),
    ]));

    private static string EntryText(SettingsSearchEntry entry) => Normalize(Join([entry.Label, entry.Description, .. entry.Keywords]));

    private static string Join(IEnumerable<string?> parts) => string.Join(' ', parts.Where(part => !string.IsNullOrEmpty(part)));
}
