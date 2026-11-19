using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// The profile's date and time formats (<c>ui.date_format</c>, <c>ui.time_format</c>) as the web applies them
/// (lib/datetime.ts: formatDate, preferredDateLocale, formatTime, formatDateTime; lib/appLocale.ts:
/// pinnedLocaleUnlessLocalized). The expected texts are Intl's for the same locale and options.
/// </summary>
public sealed class DateTextPreferenceTests
{
    private static readonly DateTime Pilot = new(2013, 9, 12, 15, 4, 9);
    private static readonly DateTime Morning = new(2013, 9, 12, 9, 4, 9);

    // Texts in a language, dates and numbers in a format locale, and the profile's formats.
    private static IDisposable Show(string language, string formats, string? date = null, string? time = null)
    {
        var texts = AppText.Use(TextLanguage.Load(language, formats: FormatLocale.For(formats)));
        var preferences = DateText.Use(DateTimePreferences.Parse(date, time));
        return new Both(texts, preferences);
    }

    [Fact]
    public void EnglishWithDayMonthYear_WritesAMediumDateInBritishOrder()
    {
        // The case reported: the app in English, its formats English (chosen on a Swiss German Windows), DD/MM/YYYY.
        using var _ = Show("en", "en", DateTimePreferences.DayMonthYear);

        Assert.Equal("12 Sept 2013", DateText.MediumDate(Pilot));
    }

    [Fact]
    public void EnglishTextsOnAGermanFormat_StillPinTheEnglishOrder()
    {
        // Dates come out in German only once the language on screen is German too (localizedFormats).
        using var _ = Show("en", "de-CH", DateTimePreferences.DayMonthYear);

        Assert.Equal("12 Sept 2013", DateText.MediumDate(Pilot));
    }

    [Theory]
    [InlineData(DateTimePreferences.Auto, "9/12/2013")]
    [InlineData(DateTimePreferences.DayMonthYear, "12/09/2013")]
    [InlineData(DateTimePreferences.MonthDayYear, "09/12/2013")]
    [InlineData(DateTimePreferences.YearMonthDay, "2013-09-12")]
    public void ANumericDate_FollowsTheDateFormat(string format, string expected)
    {
        using var _ = Show("en", "en", format);

        Assert.Equal(expected, DateText.Date(Pilot));
    }

    [Theory]
    [InlineData(DateTimePreferences.Auto, "Sep 12, 2013")]
    [InlineData(DateTimePreferences.DayMonthYear, "12 Sept 2013")]
    [InlineData(DateTimePreferences.MonthDayYear, "Sep 12, 2013")]
    [InlineData(DateTimePreferences.YearMonthDay, "2013-09-12")]
    public void AMediumDate_FollowsTheDateFormat(string format, string expected)
    {
        using var _ = Show("en", "en", format);

        Assert.Equal(expected, DateText.MediumDate(Pilot));
    }

    [Theory]
    [InlineData(DateTimePreferences.DayMonthYear, "12/09/2013")]
    [InlineData(DateTimePreferences.MonthDayYear, "09/12/2013")]
    [InlineData(DateTimePreferences.YearMonthDay, "2013-09-12")]
    public void ANumericDate_TakesTheExactPattern_InAnyLanguage(string format, string expected)
    {
        using var _ = Show("de", "de", format);

        Assert.Equal(expected, DateText.Date(Pilot));
    }

    [Theory]
    [InlineData(DateTimePreferences.DayMonthYear)]
    [InlineData(DateTimePreferences.MonthDayYear)]
    public void InGerman_AMediumDate_StaysGerman(string format)
    {
        // Once dates come out in a non-English language, its month names bring its order: nothing is pinned.
        string german;
        using (Show("de", "de"))
        {
            german = DateText.MediumDate(Pilot);
        }
        using var _ = Show("de", "de", format);

        Assert.Equal(german, DateText.MediumDate(Pilot));
        Assert.StartsWith("12. Sep", german, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DateTimePreferences.Auto, "Sep 12")]
    [InlineData(DateTimePreferences.DayMonthYear, "12 Sept")]
    [InlineData(DateTimePreferences.MonthDayYear, "Sep 12")]
    [InlineData(DateTimePreferences.YearMonthDay, "Sep 12")]
    public void AFragment_TakesTheOrderOfTheDateFormat_AndIsoTheLocales(string format, string expected)
    {
        // The web's toLocaleDateString(preferredDateLocale(), { month: "short", day: "numeric" }).
        using var _ = Show("en", "en", format);

        Assert.Equal(expected, DateText.Format(Pilot, DateText.MonthDay));
    }

    [Fact]
    public void ACalendarLabel_KeepsTheFormatLocale()
    {
        // lib/calendarWeek.ts formats with formatLocale(), never the preferred one.
        using var _ = Show("en", "en", DateTimePreferences.DayMonthYear);

        Assert.Equal("Sep 12", DateText.InFormatLocale(Pilot, DateText.MonthDay));
    }

    [Fact]
    public void TwentyFourHours_OnATwelveHourLocale_PadsTheHour()
    {
        using var _ = Show("en", "en", time: DateTimePreferences.TwentyFourHours);

        Assert.Equal("15:04", DateText.Time(Pilot));
        Assert.Equal("09:04", DateText.Time(Morning));
        Assert.Equal("09:04:09", DateText.Format(Morning, DateText.HourMinuteSecond));
    }

    [Fact]
    public void TwelveHours_OnATwentyFourHourLocale_HasADayPeriod()
    {
        using var _ = Show("en", "en-GB", time: DateTimePreferences.TwelveHours);

        Assert.Matches("^3:04[  ](pm|PM)$", DateText.Time(Pilot));
    }

    [Fact]
    public void DateAndTime_FollowBoth()
    {
        using var _ = Show("en", "en", DateTimePreferences.YearMonthDay, DateTimePreferences.TwentyFourHours);

        Assert.Equal("2013-09-12, 15:04:09", DateText.DateTime(Pilot));
        Assert.Equal("2013-09-12, 09:04", DateText.DateTime(Morning, seconds: false, medium: true));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("en", "de-CH")]
    [InlineData("de", "de")]
    [InlineData("fr", "fr")]
    public void Auto_ChangesNothing(string language, string formats)
    {
        string[] without;
        using (AppText.Use(TextLanguage.Load(language, formats: FormatLocale.For(formats))))
        {
            without = Everything();
        }
        using var _ = Show(language, formats, DateTimePreferences.Auto, DateTimePreferences.Auto);

        Assert.Equal(without, Everything());
    }

    [Fact]
    public void AnUnknownValue_ReadsAsAuto()
    {
        Assert.Same(DateTimePreferences.Default, DateTimePreferences.Parse("DD.MM.YYYY", "13h"));
    }

    private static string[] Everything() =>
    [
        DateText.Date(Pilot), DateText.MediumDate(Pilot), DateText.Time(Morning), DateText.DateTime(Pilot),
        DateText.Format(Pilot, DateText.MonthDay), DateText.Format(Pilot, DateText.LongDay), DateText.Format(Morning, DateText.Hour),
    ];

    private sealed class Both(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            second.Dispose();
            first.Dispose();
        }
    }
}
