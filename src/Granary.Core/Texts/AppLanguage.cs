namespace Granary.Core.Texts;

/// <summary>
/// Which language Granary shows, by Silo's rules (docs/architecture/i18n.md, "Choosing the language at runtime"; the
/// web's <c>lib/appLocale.ts</c>, <c>matchShippedLanguage</c> and <c>resolveAppLocale</c>). As in Silo's apps, only
/// enabled languages are chosen, from the profile's setting as from Windows' list.
/// </summary>
public static class AppLanguage
{
    public const string English = "en";

    // Where CLDR's likely subtags give Chinese the Traditional script (Intl.Locale.maximize, CLDR 48): there zh-XX is
    // zh-Hant-XX, which Granary does not ship. UK, PZ and VD are old names of GB, PA and VN.
    private static readonly HashSet<string> TraditionalChineseRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "AU", "BN", "GB", "GF", "HK", "ID", "MO", "PA", "PF", "PH", "SR", "TH", "TW", "US", "VN", "UK", "PZ", "VD",
    };

    // Language names a stored value may hold, as the web's canonicalLanguageTag reads them (lib/languageTags.ts).
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arabic"] = "ar",
        ["english"] = "en",
        ["spanish"] = "es",
        ["french"] = "fr",
        ["german"] = "de",
        ["portuguese"] = "pt",
        ["brazilian portuguese"] = "pt-BR",
        ["portuguese (brazil)"] = "pt-BR",
        ["chinese (traditional)"] = "zh-Hant",
        ["traditional chinese"] = "zh-Hant",
    };

    /// <summary>The languages Silo's web ships and Granary has catalogs for, in the web's order (<c>SHIPPED_LANGUAGES</c>).</summary>
    public static IReadOnlyList<string> Shipped { get; } = ["en", "es", "de", "fr", "pt-BR", "ja", "zh-Hans", "ar"];

    /// <summary>
    /// The languages a user can get. English only until every area of Granary shows its texts through the catalogs,
    /// so nobody sees a half-translated Granary; the other catalogs ship meanwhile.
    /// </summary>
    public static IReadOnlyList<string> Enabled { get; } = [English];

    /// <summary>
    /// The shipped language a tag selects, or null: the one with the same language and script once both are filled in
    /// with their likely script, so a regional tag gets its language (<c>es-MX</c> → <c>es</c>, <c>pt-PT</c> →
    /// <c>pt-BR</c>) and Chinese follows its script (<c>zh-CN</c>, <c>zh-SG</c> → <c>zh-Hans</c>; <c>zh-TW</c> → none).
    /// </summary>
    public static string? Match(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || !TryParse(tag, out string language, out string? script, out string? region))
        {
            return null;
        }
        string? likelyScript = script ?? LikelyScript(language, region);
        return (language, likelyScript?.ToLowerInvariant()) switch
        {
            ("en", "latn") => "en",
            ("es", "latn") => "es",
            ("de", "latn") => "de",
            ("fr", "latn") => "fr",
            ("pt", "latn") => "pt-BR",
            ("ja", "jpan") => "ja",
            ("zh", "hans") => "zh-Hans",
            ("ar", "arab") => "ar",
            _ => null,
        };
    }

    /// <summary>
    /// The language to show, highest precedence first: the profile's <c>ui.app_language</c> (<paramref name="stored"/>,
    /// null to follow the device), then the first of Windows' <paramref name="preferred"/> languages, then English; a
    /// language counts only when it is <paramref name="enabled"/>. English is always enabled, and found in Windows' list it
    /// ends the search, as on the web.
    /// </summary>
    public static string Resolve(string? stored, IEnumerable<string> preferred, IReadOnlyCollection<string> enabled)
    {
        bool IsEnabled(string? language) =>
            language == English || (language is not null && enabled.Contains(language, StringComparer.OrdinalIgnoreCase));

        string? chosen = Match(Canonical(stored));
        if (IsEnabled(chosen))
        {
            return chosen!;
        }
        return preferred.Select(Match).FirstOrDefault(IsEnabled) ?? English;
    }

    /// <summary>Whether a tag's language is written right to left, among the shipped ones (the web's <c>interfaceDirection</c>).</summary>
    public static bool IsRightToLeft(string tag) => Match(tag) == "ar";

    // A stored value as the web reads it: trimmed, a language name taken for its tag, and only a two- or three-letter
    // language accepted.
    private static string? Canonical(string? stored)
    {
        if (stored is null)
        {
            return null;
        }
        string trimmed = (LanguageNames.TryGetValue(stored.Trim(), out string? named) ? named : stored).Trim();
        return TryParse(trimmed, out string language, out _, out _) && language.Length is 2 or 3 ? trimmed : null;
    }

    private static string? LikelyScript(string language, string? region) => language switch
    {
        "en" or "es" or "de" or "fr" or "pt" => "Latn",
        "ja" => "Jpan",
        "ar" => "Arab",
        "zh" => region is not null && TraditionalChineseRegions.Contains(region) ? "Hant" : "Hans",
        _ => null,
    };

    // A BCP 47 tag as Intl.Locale takes it ('_' read as '-'): a language, then an optional script and region, then
    // variants, extensions and private use, which matching ignores. A malformed tag selects nothing, as Intl.Locale
    // refuses it.
    private static bool TryParse(string tag, out string language, out string? script, out string? region)
    {
        language = "";
        script = null;
        region = null;
        string[] parts = tag.Replace('_', '-').Split('-');
        if (parts.Any(part => part.Length is 0 or > 8 || !part.All(char.IsAsciiLetterOrDigit)))
        {
            return false;
        }
        if (parts[0].Length is not (>= 2 and <= 3) and not (>= 5 and <= 8) || !parts[0].All(char.IsAsciiLetter))
        {
            return false;
        }
        language = parts[0].ToLowerInvariant();
        int next = 1;
        if (next < parts.Length && parts[next].Length == 4 && parts[next].All(char.IsAsciiLetter))
        {
            script = parts[next++];
        }
        if (next < parts.Length && IsRegion(parts[next]))
        {
            region = parts[next++];
        }
        return IsWellFormedRest(parts.AsSpan(next));
    }

    // What follows the region: variants, each once; then extensions, each a singleton that comes once with at least one
    // subtag of two characters or more (de-u is no tag); then private use, x with at least one subtag. What an extension's
    // subtags say (-u-, -t-) is not checked further.
    private static bool IsWellFormedRest(ReadOnlySpan<string> parts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int next = 0;
        while (next < parts.Length && IsVariant(parts[next]))
        {
            if (!seen.Add(parts[next++]))
            {
                return false;
            }
        }
        while (next < parts.Length && parts[next].Length == 1 && !IsPrivateUse(parts[next]))
        {
            if (!seen.Add(parts[next++]))
            {
                return false;
            }
            int first = next;
            while (next < parts.Length && parts[next].Length >= 2)
            {
                next++;
            }
            if (next == first)
            {
                return false;
            }
        }
        // After x, every subtag is private: any one to eight characters, which all parts are already.
        return next == parts.Length || (IsPrivateUse(parts[next]) && next + 1 < parts.Length);
    }

    private static bool IsPrivateUse(string part) => part is "x" or "X";

    private static bool IsRegion(string part) =>
        (part.Length == 2 && part.All(char.IsAsciiLetter)) || (part.Length == 3 && part.All(char.IsAsciiDigit));

    private static bool IsVariant(string part) =>
        part.Length is >= 5 and <= 8 || (part.Length == 4 && char.IsAsciiDigit(part[0]));
}
