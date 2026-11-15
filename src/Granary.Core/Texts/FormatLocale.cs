using System.Globalization;

namespace Granary.Core.Texts;

/// <summary>
/// The locale dates and numbers are written in, the web's <c>formatLocale</c> (lib/appLocale.ts): Windows' regional
/// format, or the chosen language's own where that differs (<see cref="AppLanguage.FormatCulture"/>). Its digits are its
/// numbering system's, as Intl's are: Arabic-Indic for Arabic in most places (<c>ar</c>, <c>ar-EG</c>), Latin in others
/// (<c>ar-AE</c>), Latin for the other shipped languages' own cultures; a Windows format of another script may bring
/// its own (Persian on <c>fa-IR</c>).
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
        // A sort order Windows may name after the culture (de-DE_phoneb) is no part of the locale.
        string name = culture.Name.Split('_')[0];
        IcuName = name.Length == 0 ? AppLanguage.English : name.Replace('-', '_');
        GroupsFromFiveDigits = IcuNumbers.GroupsFourDigits(IcuName) is { } groups ? !groups : AppLanguage.Match(name) == "es";
        HourCycle = HourCycleOf(culture.DateTimeFormat.ShortTimePattern);
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
    /// CLDR's minimumGroupingDigits 2, as ICU's number formatter gives it: Spanish writes 1234 but 12.345, as
    /// Intl.NumberFormat does; .NET always groups. Without ICU, Spanish alone.
    /// </summary>
    internal bool GroupsFromFiveDigits { get; }

    /// <summary>
    /// The clock of the culture's short time, <c>H</c> for 24 hours or <c>h</c> for 12, as Windows' own time format sets
    /// it (a user's choice included); null to take the locale's (<c>j</c>). The web's 12h and 24h preference is the same.
    /// </summary>
    internal char? HourCycle { get; }

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

    // H or h in a .NET time pattern, outside its quoted text.
    private static char? HourCycleOf(string pattern)
    {
        bool quoted = false;
        foreach (char c in pattern)
        {
            if (c is '\'' or '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is 'H' or 'h')
            {
                return c;
            }
        }
        return null;
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
