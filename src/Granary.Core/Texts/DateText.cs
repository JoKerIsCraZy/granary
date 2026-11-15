namespace Granary.Core.Texts;

/// <summary>
/// Dates and times as the web writes them (lib/datetime.ts, and its <c>toLocaleDateString(formatLocale(), …)</c>
/// calls): in the language on screen's format locale (<see cref="TextLanguage.Formats"/>), by the fields Intl is asked
/// for, given here as their ICU skeleton (<c>yMMMd</c> is <c>{ year: "numeric", month: "short", day: "numeric" }</c>).
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

    /// <summary>A date in digits, the web's <c>formatDate(value)</c>.</summary>
    public static string Date(DateTime value) => Format(value, NumericDate);

    /// <summary>A date with its month's short name, the web's <c>formatDate(value, "medium")</c>.</summary>
    public static string MediumDate(DateTime value) => Format(value, MediumDay);

    /// <summary>A time of day, the web's <c>formatTime(value)</c>.</summary>
    public static string Time(DateTime value) => Format(value, HourMinute);

    /// <summary>
    /// A date and its time, the web's <c>formatDateTime(value, { dateStyle, seconds })</c>: "11/5/2026, 3:04:09 PM", or
    /// with <paramref name="medium"/> "Nov 5, 2026, 3:04 PM".
    /// </summary>
    public static string DateTime(DateTime value, bool seconds = true, bool medium = false) =>
        $"{Format(value, medium ? MediumDay : NumericDate)}, {Format(value, seconds ? HourMinuteSecond : HourMinute)}";

    /// <summary>
    /// <paramref name="value"/> with the fields of <paramref name="skeleton"/> (one of this class's constants), in the format
    /// locale's best pattern for them.
    /// </summary>
    public static string Format(DateTime value, string skeleton)
    {
        var locale = AppText.Language.Formats;
        return IcuDates.Format(locale.IcuName, skeleton, value) ?? Fallback(value, skeleton, locale);
    }

    // Without ICU, .NET's own pattern nearest the fields: the same parts, in places in another shape.
    private static string Fallback(DateTime value, string skeleton, FormatLocale locale)
    {
        string format = skeleton switch
        {
            NumericDate => "d",
            MediumDay => "D",
            HourMinute => "t",
            MonthDay or WeekdayMonthDay or LongMonthDay => "M",
            LongDay => "D",
            HourMinuteSecond => "T",
            Hour => "%H",
            Weekday => "dddd",
            ShortWeekday => "ddd",
            Month => "MMMM",
            ShortMonth => "MMM",
            MonthYear => "Y",
            _ => "G",
        };
        return locale.Localize(value.ToString(format, locale.Culture));
    }
}
