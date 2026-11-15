using System.Globalization;

namespace Granary.Core.Texts;

/// <summary>
/// Numbers outside messages, as the web writes them: with its format locale (<see cref="TextLanguage.Formats"/>) where it
/// calls <c>formatDecimal</c> (lib/numberFormat.ts) or <c>toLocaleString(formatLocale())</c>, and in the messages' digits
/// where it calls <c>numberText</c> (i18n/format.ts). Never send, store or compare what comes out: those keep plain
/// Latin digits.
/// </summary>
public static class NumberText
{
    /// <summary>
    /// The web's <c>formatDecimal</c>: <paramref name="value"/> with up to <paramref name="maxFractionDigits"/> decimals, and
    /// at least <paramref name="minFractionDigits"/>, in the format locale and without grouping ("1234.5", "1234,5"). It
    /// rounds as JavaScript's <c>toFixed</c> does, the binary value itself, a true half away from zero.
    /// </summary>
    public static string Decimal(double value, int maxFractionDigits, int minFractionDigits)
    {
        var locale = AppText.Language.Formats;
        string format = "0" + (maxFractionDigits > 0 ? "." + new string('0', minFractionDigits) + new string('#', maxFractionDigits - minFractionDigits) : "");
        return locale.Localize(ToFixed(value, maxFractionDigits).ToString(format, locale.Numbers));
    }

    /// <summary><see cref="Decimal(double, int, int)"/> with exactly <paramref name="fractionDigits"/> decimals.</summary>
    public static string Decimal(double value, int fractionDigits) => Decimal(value, fractionDigits, fractionDigits);

    /// <summary>
    /// The web's <c>value.toLocaleString(formatLocale())</c>: grouped, with up to three decimals ("1,234.5", "1.234,5",
    /// "1’234.5").
    /// </summary>
    public static string Grouped(double value) =>
        double.IsFinite(value) ? Grouped((decimal)value, AppText.Language.Formats) : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The web's <c>numberText</c>: Latin digits in <paramref name="text"/> ("1:05:09", a year) in the digits messages use,
    /// which are Arabic-Indic only in Arabic on a format locale that writes them. Codes (S01E05) keep Latin digits.
    /// </summary>
    public static string Digits(string text) => FormatLocale.Localize(text, AppText.Language.MessageDigits);

    /// <summary>A whole number without grouping in the messages' digits, the web's <c>numberText(value)</c>.</summary>
    public static string Digits(long value) => Digits(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>A number grouped in <paramref name="locale"/>'s way, rounded to three decimals as Intl.NumberFormat's default.</summary>
    internal static string Grouped(decimal number, FormatLocale locale) =>
        locale.Localize(Grouped(number, locale.Numbers, locale.GroupsFromFiveDigits));

    // Intl.NumberFormat's default: grouped (in Spanish from five digits on), up to three decimals, a half away from zero.
    internal static string Grouped(decimal number, NumberFormatInfo numbers, bool groupsFromFiveDigits)
    {
        bool ungrouped = groupsFromFiveDigits && Math.Abs(decimal.Truncate(Math.Round(number, 3, MidpointRounding.AwayFromZero))) < 10_000;
        return number.ToString(ungrouped ? "0.###" : "#,0.###", numbers);
    }

    // value.toFixed(digits): the exact binary value rounded, a true half away from zero. .NET's "F" rounds a half to
    // even and its custom formats round a shortened value, so the decision is made on the exact digits.
    private static decimal ToFixed(double value, int digits)
    {
        if (!double.IsFinite(value) || Math.Abs(value) >= 1e15)
        {
            return double.IsFinite(value) ? (decimal)Math.Round(value) : 0;
        }
        string exact = Math.Abs(value).ToString("F" + (digits + 20), CultureInfo.InvariantCulture);
        int point = exact.IndexOf('.');
        decimal kept = decimal.Parse(exact[..(point + 1 + digits)].TrimEnd('.'), CultureInfo.InvariantCulture);
        if (exact[point + 1 + digits] >= '5')
        {
            kept += 1m / Pow10(digits);
        }
        return value < 0 ? -kept : kept;
    }

    private static decimal Pow10(int digits)
    {
        decimal result = 1;
        for (int i = 0; i < digits; i++)
        {
            result *= 10;
        }
        return result;
    }
}
