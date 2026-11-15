using System.Globalization;

namespace Granary.Core.Texts;

/// <summary>
/// The locale dates and numbers are written in, the web's <c>formatLocale</c> (lib/appLocale.ts): Windows' regional
/// format, or the chosen language's own where that differs (<see cref="AppLanguage.FormatCulture"/>). Its digits are its
/// numbering system's, as Intl's are: Arabic-Indic for Arabic in most places (<c>ar</c>, <c>ar-EG</c>), Latin in others
/// (<c>ar-AE</c>), Latin for every other shipped language.
/// </summary>
public sealed class FormatLocale
{
    private const string LatinDigits = "0123456789";

    private FormatLocale(CultureInfo culture)
    {
        Culture = culture;
        Numbers = culture.NumberFormat;
        string[] native = Numbers.NativeDigits;
        string digits = native.Length == 10 && native.All(digit => digit.Length == 1) ? string.Concat(native) : LatinDigits;
        Digits = digits == LatinDigits ? null : digits;
        IcuName = culture.Name.Length == 0 ? AppLanguage.English : culture.Name.Replace('-', '_');
        GroupsFromFiveDigits = AppLanguage.Match(culture.Name) == "es";
    }

    /// <summary>The culture dates and numbers follow.</summary>
    public CultureInfo Culture { get; }

    /// <summary>The digits zero to nine where the locale writes other than Latin ones; null for Latin.</summary>
    internal string? Digits { get; }

    /// <summary>The locale's id for ICU (<c>de_CH</c>), which writes its dates as Intl does.</summary>
    internal string IcuName { get; }

    /// <summary>Marks and signs; its digits are Latin, which <see cref="Localize"/> turns into the locale's.</summary>
    internal NumberFormatInfo Numbers { get; }

    /// <summary>
    /// CLDR's minimumGroupingDigits 2: Spanish writes 1234 but 12.345, as Intl.NumberFormat does; .NET always groups.
    /// </summary>
    internal bool GroupsFromFiveDigits { get; }

    /// <summary>The locale of a culture.</summary>
    public static FormatLocale For(CultureInfo culture) => new(culture);

    /// <summary>The locale of a language tag; the invariant culture's without the system's culture data.</summary>
    public static FormatLocale For(string tag)
    {
        try
        {
            return new FormatLocale(CultureInfo.GetCultureInfo(tag));
        }
        catch (CultureNotFoundException)
        {
            // Only without the system's culture data (invariant globalization); a text still shows.
            return new FormatLocale(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Latin digits in <paramref name="text"/> written in the locale's digits; the text itself where those are Latin.</summary>
    internal string Localize(string text) => Localize(text, Digits);

    internal static string Localize(string text, string? digits)
    {
        if (digits is null || !text.Any(char.IsAsciiDigit))
        {
            return text;
        }
        return string.Create(text.Length, (text, digits), static (span, state) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                char c = state.text[i];
                span[i] = char.IsAsciiDigit(c) ? state.digits[c - '0'] : c;
            }
        });
    }
}
