using Granary.Core.Texts;

namespace Granary.Core.Settings.Search;

/// <summary>A group of the settings' sidebar: its list's name, the key of its heading and its entries.</summary>
public sealed record SettingsSidebarGroup(string Id, string LabelKey, IReadOnlyList<SettingsSidebarPage> Pages);

/// <summary>
/// What the settings' search goes through: the sidebar's groups and pages, each with what its page shows, read from the
/// markup (<see cref="SettingsMarkup"/>) and written in the language on screen. In another language the English texts are
/// keywords too, so a search typed in English still finds them, as on the web (lib/userSettingsNav.ts, <c>englishTerms</c>).
/// </summary>
public static class SettingsSearchIndex
{
    /// <param name="pageOf">What a page shows, by its tag; null for a page whose markup could not be read.</param>
    public static IReadOnlyList<SettingsSearchGroup> Build(IReadOnlyList<SettingsSidebarGroup> groups, Func<string, SettingsMarkupPage?> pageOf)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(pageOf);
        var texts = new Texts(AppText.Language, AppText.English);
        return
        [
            .. groups.Select(group => new SettingsSearchGroup(group.Id, texts.Of(group.LabelKey), [],
            [
                .. group.Pages.Select(page => pageOf(page.Tag) is { } markup
                    ? new SettingsSearchPage(page.Tag, texts.Of(page.LabelKey), texts.OfOptional(markup.DescriptionKey),
                        [.. page.Keywords, .. texts.English(group.LabelKey, page.LabelKey, markup.DescriptionKey)],
                        [.. markup.Entries.Select(texts.Entry)])
                    : new SettingsSearchPage(page.Tag, texts.Of(page.LabelKey), null,
                        [.. page.Keywords, .. texts.English(group.LabelKey, page.LabelKey)], [])),
            ])),
        ];
    }

    private sealed class Texts(TextLanguage language, TextLanguage english)
    {
        private readonly bool _isEnglish = language.Tag == english.Tag;

        public string Of(string key) => language.Get(key);

        public string? OfOptional(string? key) => key is null ? null : language.Get(key);

        public SettingsSearchEntry Entry(SettingsMarkupEntry entry) => new(entry.Kind, Of(entry.LabelKey), OfOptional(entry.DescriptionKey),
            [.. entry.Keywords, .. English(entry.LabelKey, entry.DescriptionKey)]);

        // The English texts the language on screen words otherwise; none in English, where they are the texts shown.
        public IEnumerable<string> English(params string?[] keys) => _isEnglish
            ? []
            : keys.OfType<string>().Select(key => (Shown: language.Get(key), English: english.Get(key)))
                .Where(text => !string.Equals(text.Shown, text.English, StringComparison.Ordinal))
                .Select(text => text.English);
    }
}
