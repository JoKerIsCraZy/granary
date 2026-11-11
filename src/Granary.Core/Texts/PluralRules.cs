namespace Granary.Core.Texts;

/// <summary>
/// CLDR's cardinal plural rules for the languages Silo's web ships, the categories its catalogs' plurals carry: en and
/// de one/other, es, fr and pt-BR one/many/other, ja and zh other only, ar zero/one/two/few/many/other
/// (docs/architecture/i18n.md, "Messages"). As <c>Intl.PluralRules</c> does, a count is first rounded to three
/// decimals and read without trailing zeros, so 1.0 is "one" in English.
/// </summary>
internal static class PluralRules
{
    public const string Other = "other";

    /// <summary>The CLDR category of a count in a language; <c>other</c> in a language without rules here.</summary>
    public static string Category(string tag, decimal count)
    {
        decimal n = Math.Abs(Math.Round(count, 3, MidpointRounding.AwayFromZero));
        decimal i = decimal.Truncate(n);
        bool isWhole = n == i;
        return Language(tag) switch
        {
            "en" or "de" => isWhole && i == 1 ? "one" : Other,
            "es" => n == 1 ? "one" : Millions(i, isWhole) ? "many" : Other,
            "fr" or "pt" => i is 0 or 1 ? "one" : Millions(i, isWhole) ? "many" : Other,
            "ar" => Arabic(n, isWhole),
            _ => Other,
        };
    }

    // es, fr, pt: "e = 0 and i != 0 and i % 1000000 = 0 and v = 0" (a compact exponent never applies here).
    private static bool Millions(decimal i, bool isWhole) => isWhole && i != 0 && i % 1_000_000 == 0;

    private static string Arabic(decimal n, bool isWhole)
    {
        if (!isWhole)
        {
            return Other;
        }
        decimal hundreds = n % 100;
        return n switch
        {
            0 => "zero",
            1 => "one",
            2 => "two",
            _ when hundreds is >= 3 and <= 10 => "few",
            _ when hundreds is >= 11 and <= 99 => "many",
            _ => Other,
        };
    }

    private static string Language(string tag)
    {
        int dash = tag.IndexOfAny(['-', '_']);
        return (dash < 0 ? tag : tag[..dash]).ToLowerInvariant();
    }
}
