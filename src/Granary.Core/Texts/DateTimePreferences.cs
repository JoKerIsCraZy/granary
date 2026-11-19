namespace Granary.Core.Texts;

/// <summary>
/// The profile's date and time formats, <c>ui.date_format</c> and <c>ui.time_format</c>, as the web's
/// <c>DateTimeFormatPreferences</c> (lib/datetime.ts): the order of a date's day, month and year, and the clock. Auto
/// leaves both to the format locale, and Windows' own clock. A value neither knows reads as auto, as the web parses it.
/// </summary>
public sealed record DateTimePreferences
{
    public const string Auto = "auto";

    /// <summary>"12/09/2013"; dates with a month's name take the British order, "12 Sep 2013".</summary>
    public const string DayMonthYear = "DD/MM/YYYY";

    /// <summary>"09/12/2013"; dates with a month's name take the American order, "Sep 12, 2013".</summary>
    public const string MonthDayYear = "MM/DD/YYYY";

    /// <summary>"2013-09-12", for a date with its month's name too; fragments without a year keep the locale's order.</summary>
    public const string YearMonthDay = "YYYY-MM-DD";

    public const string TwelveHours = "12h";

    public const string TwentyFourHours = "24h";

    private DateTimePreferences(string dateFormat, string timeFormat)
    {
        DateFormat = dateFormat;
        TimeFormat = timeFormat;
    }

    /// <summary>The web's <c>DATE_FORMAT_PREFERENCES</c>, in its order.</summary>
    public static IReadOnlyList<string> DateFormats { get; } = [Auto, DayMonthYear, MonthDayYear, YearMonthDay];

    /// <summary>The web's <c>TIME_FORMAT_PREFERENCES</c>, in its order.</summary>
    public static IReadOnlyList<string> TimeFormats { get; } = [Auto, TwelveHours, TwentyFourHours];

    /// <summary>Both auto: the format locale's dates on Windows' clock.</summary>
    public static DateTimePreferences Default { get; } = new(Auto, Auto);

    /// <summary>One of <see cref="DateFormats"/>.</summary>
    public string DateFormat { get; }

    /// <summary>One of <see cref="TimeFormats"/>.</summary>
    public string TimeFormat { get; }

    /// <summary>The preferences of two stored values, each auto unless it is one the web knows (its parse functions).</summary>
    public static DateTimePreferences Parse(string? dateFormat, string? timeFormat)
    {
        string date = Known(DateFormats, dateFormat);
        string time = Known(TimeFormats, timeFormat);
        return date == Auto && time == Auto ? Default : new DateTimePreferences(date, time);
    }

    /// <summary>These preferences with another date format.</summary>
    public DateTimePreferences WithDateFormat(string? dateFormat) => Parse(dateFormat, TimeFormat);

    /// <summary>These preferences with another time format.</summary>
    public DateTimePreferences WithTimeFormat(string? timeFormat) => Parse(DateFormat, timeFormat);

    private static string Known(IReadOnlyList<string> known, string? value) =>
        value is not null && known.Contains(value, StringComparer.Ordinal) ? value : Auto;
}
