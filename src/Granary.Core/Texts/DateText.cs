using System.Globalization;

namespace Granary.Core.Texts;

/// <summary>
/// Dates and times as the web writes them (lib/datetime.ts, and its <c>toLocaleDateString(formatLocale(), …)</c>
/// calls): in the language on screen's format locale (<see cref="TextLanguage.Formats"/>), by the fields Intl is asked
/// for, given here as their ICU skeleton (<c>yMMMd</c> is <c>{ year: "numeric", month: "short", day: "numeric" }</c>).
/// The profile's date and time formats (<see cref="Preferences"/>) set the order and the clock, as the web's do.
/// A value is written as it reads, its own day and hour, never moved into another zone: convert it first. Never send,
/// store or compare what comes out.
/// </summary>
public static class DateText
{
    /// <summary>"11/5/2026", "5.11.2026": the web's <c>formatDate(value)</c>, a date in digits.</summary>
    public const string NumericDate = "yMd";

    /// <summary>"Nov 5, 2026", "5. Nov. 2026": the web's <c>formatDate(value, "medium")</c>.</summary>
    public const string MediumDay = "yMMMd";

    /// <summary>"3:04 PM", "15:04": the web's <c>formatTime(value)</c>, an hour and its minutes on the locale's clock.</summary>
    public const string HourMinute = "jmm";

    /// <summary>"3 PM", "15 Uhr": <c>formatTime(value, { hour: "numeric", minute: undefined })</c>, an hour alone.</summary>
    public const string Hour = "j";

    /// <summary>"Nov 5", "5. Nov.": <c>{ month: "short", day: "numeric" }</c>.</summary>
    public const string MonthDay = "MMMd";

    /// <summary>"November 5", "5. November": <c>{ month: "long", day: "numeric" }</c>.</summary>
    public const string LongMonthDay = "MMMMd";

    /// <summary>"November 5, 2026", "5. November 2026": <c>{ day: "numeric", month: "long", year: "numeric" }</c>.</summary>
    public const string LongDay = "yMMMMd";

    /// <summary>"3:04:09 PM", "15:04:09": the web's <c>formatTime(value, { second: "2-digit" })</c>.</summary>
    public const string HourMinuteSecond = "jmmss";

    /// <summary>"Thu, Nov 5", "Do., 5. Nov.": <c>{ weekday: "short", month: "short", day: "numeric" }</c>.</summary>
    public const string WeekdayMonthDay = "MMMEd";

    /// <summary>"Thursday": <c>{ weekday: "long" }</c>.</summary>
    public const string Weekday = "EEEE";

    /// <summary>"Thu": <c>{ weekday: "short" }</c>.</summary>
    public const string ShortWeekday = "EEE";

    /// <summary>"November": <c>{ month: "long" }</c>.</summary>
    public const string Month = "MMMM";

    /// <summary>"Nov": <c>{ month: "short" }</c>.</summary>
    public const string ShortMonth = "MMM";

    /// <summary>"November 2026": <c>{ month: "long", year: "numeric" }</c>.</summary>
    public const string MonthYear = "yMMMM";

    private static readonly AsyncLocal<DateTimePreferences?> InThisFlow = new();
    private static readonly Lazy<FormatLocale> British = new(() => FormatLocale.For("en-GB"));
    private static readonly Lazy<FormatLocale> American = new(() => FormatLocale.For("en-US"));
    private static DateTimePreferences? _applied;

    /// <summary>
    /// The profile's date and time formats dates are written in now (the web's <c>getDateTimeFormatPreferences</c>): as kept
    /// on this PC when Granary starts, then the profile's as read or chosen.
    /// </summary>
    public static DateTimePreferences Preferences => InThisFlow.Value ?? Volatile.Read(ref _applied) ?? DateTimePreferences.Default;

    /// <summary>Makes <paramref name="preferences"/> the process's, for every date written from now on.</summary>
    public static void Apply(DateTimePreferences preferences) => Volatile.Write(ref _applied, preferences);

    /// <summary>Uses <paramref name="preferences"/> in the calling async flow only, until disposed: for tests, which run in parallel.</summary>
    public static IDisposable Use(DateTimePreferences preferences)
    {
        var before = InThisFlow.Value;
        InThisFlow.Value = preferences;
        return new Restore(before);
    }

    /// <summary>
    /// A date in digits, the web's <c>formatDate(value)</c>: the date format's exact pattern in Latin digits, or the format
    /// locale's own under auto.
    /// </summary>
    public static string Date(DateTime value) => Preferences.DateFormat switch
    {
        DateTimePreferences.DayMonthYear => Digits(value, "dd'/'MM'/'yyyy"),
        DateTimePreferences.MonthDayYear => Digits(value, "MM'/'dd'/'yyyy"),
        DateTimePreferences.YearMonthDay => Digits(value, "yyyy'-'MM'-'dd"),
        _ => Write(value, NumericDate, AppText.Language.Formats),
    };

    /// <summary>
    /// A date with its month's short name, the web's <c>formatDate(value, "medium")</c>: in the order of the date format,
    /// and in digits under YYYY-MM-DD, which has no form with a month's name.
    /// </summary>
    public static string MediumDate(DateTime value) => Preferences.DateFormat == DateTimePreferences.YearMonthDay
        ? Date(value)
        : Write(value, MediumDay, PreferredLocale());

    /// <summary>A time of day, the web's <c>formatTime(value)</c>.</summary>
    public static string Time(DateTime value) => Format(value, HourMinute);

    /// <summary>
    /// A date and its time, the web's <c>formatDateTime(value, { dateStyle, seconds })</c>: "11/5/2026, 3:04:09 PM", or
    /// with <paramref name="medium"/> "Nov 5, 2026, 3:04 PM".
    /// </summary>
    public static string DateTime(DateTime value, bool seconds = true, bool medium = false) =>
        $"{(medium ? MediumDate(value) : Date(value))}, {Format(value, seconds ? HourMinuteSecond : HourMinute)}";

    /// <summary>
    /// <paramref name="value"/> with the fields of <paramref name="skeleton"/> (one of this class's constants), in the best
    /// pattern for them: a date as the web's <c>toLocaleDateString(preferredDateLocale(), …)</c>, in the order of the date
    /// format; a time as its <c>formatTime</c>, in the format locale on the clock of the time format.
    /// </summary>
    public static string Format(DateTime value, string skeleton) =>
        Write(value, skeleton, skeleton.Contains('j', StringComparison.Ordinal) ? AppText.Language.Formats : PreferredLocale());

    /// <summary>
    /// <paramref name="value"/> with the fields of <paramref name="skeleton"/> in the format locale whatever the date format,
    /// as the web's <c>toLocaleDateString(formatLocale(), …)</c> (the calendar's week and day labels, lib/calendarWeek.ts).
    /// </summary>
    public static string InFormatLocale(DateTime value, string skeleton) => Write(value, skeleton, AppText.Language.Formats);

    // The web's preferredDateLocale: British or American English for a date format of that order, unless dates come out in
    // the language on screen (pinnedLocaleUnlessLocalized); the format locale for auto and for YYYY-MM-DD.
    private static FormatLocale PreferredLocale()
    {
        var language = AppText.Language;
        return Preferences.DateFormat switch
        {
            DateTimePreferences.DayMonthYear when !language.LocalizedFormats => British.Value,
            DateTimePreferences.MonthDayYear when !language.LocalizedFormats => American.Value,
            _ => language.Formats,
        };
    }

    // The web's pad2 patterns: Latin digits in every language.
    private static string Digits(DateTime value, string pattern) => value.ToString(pattern, CultureInfo.InvariantCulture);

    private static string Write(DateTime value, string skeleton, FormatLocale locale)
    {
        var (fields, hour) = Clock(skeleton, locale);
        return IcuDates.Format(locale.IcuName, fields, value, matchHourLength: fields.Contains("HH", StringComparison.Ordinal))
            ?? Fallback(value, skeleton, locale, Preferences.TimeFormat == DateTimePreferences.Auto ? null : hour);
    }

    // The hour of a time's skeleton (j, the locale's): 12h and 24h set it, as the web's formatTime sets hourCycle; under
    // auto, Windows' own clock (its time format, a user's choice included). 24h writes two digits ("09:04"), as the web's
    // asks for "2-digit", unless the hour alone was asked for.
    private static (string Fields, char? Hour) Clock(string skeleton, FormatLocale locale)
    {
        if (!skeleton.Contains('j', StringComparison.Ordinal))
        {
            return (skeleton, null);
        }
        string time = Preferences.TimeFormat;
        char? hour = time switch
        {
            DateTimePreferences.TwelveHours => 'h',
            DateTimePreferences.TwentyFourHours => 'H',
            _ => locale.HourCycle,
        };
        if (hour is not { } cycle)
        {
            return (skeleton, null);
        }
        string fields = time == DateTimePreferences.TwentyFourHours && skeleton != Hour ? "HH" : cycle.ToString();
        return (skeleton.Replace("j", fields, StringComparison.Ordinal), cycle);
    }

    /// <summary>Without ICU, .NET's own pattern nearest the fields: the same parts, in places in another shape.</summary>
    /// <param name="hour">The clock chosen, <c>h</c> for 12h or <c>H</c> for 24h; null keeps the culture's own time patterns.</param>
    internal static string Fallback(DateTime value, string skeleton, FormatLocale locale, char? hour = null)
    {
        string format = skeleton switch
        {
            NumericDate => "d",
            MediumDay => "D",
            HourMinute => hour switch { 'H' => "HH:mm", 'h' => "h:mm tt", _ => "t" },
            MonthDay or LongMonthDay => "M",
            // .NET has no pattern with the weekday and the day alone: the short weekday before the month and day's.
            WeekdayMonthDay => "ddd, " + locale.Culture.DateTimeFormat.MonthDayPattern,
            LongDay => "D",
            HourMinuteSecond => hour switch { 'H' => "HH:mm:ss", 'h' => "h:mm:ss tt", _ => "T" },
            Hour => (hour ?? locale.HourCycle) == 'h' ? "h tt" : "%H",
            Weekday => "dddd",
            ShortWeekday => "ddd",
            Month => "MMMM",
            ShortMonth => "MMM",
            MonthYear => "Y",
            _ => "G",
        };
        return locale.Localize(value.ToString(format, locale.Culture));
    }

    private sealed class Restore(DateTimePreferences? before) : IDisposable
    {
        public void Dispose() => InThisFlow.Value = before;
    }
}
