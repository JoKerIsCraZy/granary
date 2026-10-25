using System.Collections.Frozen;
using System.Globalization;

namespace Granary.Core.Playback;

/// <summary>
/// Track languages as Silo's clients compare them: the primary language only, with ISO 639-2 codes (what the scanner
/// usually records) folded to ISO 639-1 (Android's canonicalSubtitleLanguage).
/// </summary>
public static class TrackLanguages
{
    // ISO 639-2/B codes older scans carry, to the 639-2/T codes .NET knows.
    private static readonly FrozenDictionary<string, string> Bibliographic = new Dictionary<string, string>
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho", ["cze"] = "ces", ["dut"] = "nld",
        ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu", ["gre"] = "ell", ["ice"] = "isl", ["mac"] = "mkd", ["mao"] = "mri",
        ["may"] = "msa", ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk", ["tib"] = "bod", ["wel"] = "cym",
    }.ToFrozenDictionary();

    private static readonly Lazy<FrozenDictionary<string, CultureInfo>> ThreeLetterCultures = new(() =>
        CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Where(c => c.ThreeLetterISOLanguageName.Length == 3 && c.Name.Length > 0)
            .GroupBy(c => c.ThreeLetterISOLanguageName, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.OrderBy(c => c.Name.Length).First(), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// "en" for <c>en</c>, <c>eng</c> and <c>en-US</c>, "de" for <c>ger</c>; null for none or <c>und</c>. A code without
    /// a 639-1 form, or one nobody knows, stays as it is, lower-cased.
    /// </summary>
    public static string? Key(string? language)
    {
        string primary = Primary(language);
        if (primary.Length == 0 || primary == "und")
        {
            return null;
        }
        return primary.Length == 3 && Culture(primary) is { TwoLetterISOLanguageName.Length: 2 } culture
            ? culture.TwoLetterISOLanguageName
            : primary;
    }

    /// <summary>The neutral culture of a two- or three-letter language code (639-1, 639-2/T or 639-2/B); null when .NET has none.</summary>
    internal static CultureInfo? Culture(string primary)
    {
        primary = primary.ToLowerInvariant();
        if (primary.Length == 3)
        {
            return ThreeLetterCultures.Value.GetValueOrDefault(Bibliographic.GetValueOrDefault(primary, primary));
        }
        if (primary.Length != 2)
        {
            return null;
        }
        try
        {
            return CultureInfo.GetCultureInfo(primary, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static string Primary(string? language) =>
        (language?.Trim().Replace('_', '-').Split('-')[0] ?? "").ToLowerInvariant();
}
