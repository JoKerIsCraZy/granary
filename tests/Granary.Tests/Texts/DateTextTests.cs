using System.Globalization;
using Granary.Core.Texts;

namespace Granary.Tests.Texts;

/// <summary>
/// Dates and times as the web writes them with its format locale (lib/datetime.ts: formatDate, formatTime; the
/// toLocaleDateString calls of lib/calendarWeek.ts). The expected texts are what Intl.DateTimeFormat gives in a browser
/// for the same locale and options: both take CLDR's patterns, through ICU.
/// </summary>
public sealed class DateTextTests
{
    private static readonly DateTime Thursday = new(2026, 11, 5, 15, 4, 9);

    [Theory]
    [InlineData("en", "11/5/2026")]
    [InlineData("en-GB", "05/11/2026")]
    [InlineData("de", "5.11.2026")]
    [InlineData("fr", "05/11/2026")]
    [InlineData("es", "5/11/2026")]
    [InlineData("pt-BR", "05/11/2026")]
    [InlineData("ja", "2026/11/5")]
    [InlineData("zh-Hans", "2026/11/5")]
    [InlineData("ar", "٥‏/١١‏/٢٠٢٦")]
    [InlineData("ar-AE", "5‏/11‏/2026")]
    public void ADate_IsWrittenInDigits_AsTheFormatLocaleWritesIt(string formats, string expected)
    {
        using var _ = Formats(formats);

        Assert.Equal(expected, DateText.Date(Thursday));
    }

    [Theory]
    [InlineData("en", "Nov 5, 2026")]
    [InlineData("de", "5. Nov. 2026")]
    [InlineData("fr", "5 nov. 2026")]
    [InlineData("es", "5 nov 2026")]
    [InlineData("pt-BR", "5 de nov. de 2026")]
    [InlineData("ja", "2026年11月5日")]
    [InlineData("ar", "٥ نوفمبر ٢٠٢٦")]
    public void AMediumDate_NamesItsMonth(string formats, string expected)
    {
        using var _ = Formats(formats);

        Assert.Equal(expected, DateText.MediumDate(Thursday));
    }

    [Theory]
    [InlineData("de", "15:04")]
    [InlineData("en-GB", "15:04")]
    [InlineData("fr", "15:04")]
    [InlineData("ja", "15:04")]
    [InlineData("es", "15:04")]
    [InlineData("ar", "٣:٠٤ م")]
    public void ATime_IsOnTheFormatLocalesClock(string formats, string expected)
    {
        using var _ = Formats(formats);

        Assert.Equal(expected, DateText.Time(Thursday));
    }

    [Fact]
    public void ATime_InAmericanEnglish_HasItsDayPeriod()
    {
        using var _ = Formats("en");

        // CLDR puts a narrow no-break space before AM and PM; older ICU a plain one.
        Assert.Matches("^3:04[  ]PM$", DateText.Time(Thursday));
        Assert.Matches("^9:04[  ]AM$", DateText.Time(Thursday.AddHours(-6)));
    }

    [Theory]
    [InlineData("en", "Thu, Nov 5")]
    [InlineData("de", "Do., 5. Nov.")]
    [InlineData("fr", "jeu. 5 nov.")]
    [InlineData("ja", "11月5日(木)")]
    public void APattern_IsTheFormatLocalesBestForItsFields(string formats, string expected)
    {
        // The skeleton of Intl's { weekday: "short", month: "short", day: "numeric" }.
        using var _ = Formats(formats);

        Assert.Equal(expected, DateText.Format(Thursday, DateText.WeekdayMonthDay));
    }

    [Theory]
    [InlineData("en", "Thursday", "November", "Thu", "Nov", "November 2026")]
    [InlineData("de", "Donnerstag", "November", "Do", "Nov", "November 2026")]
    [InlineData("es", "jueves", "noviembre", "jue", "nov", "noviembre de 2026")]
    [InlineData("ja", "木曜日", "11月", "木", "11月", "2026年11月")]
    public void NamesOfDaysAndMonths_AreTheFormatLocales(
        string formats, string weekday, string month, string shortWeekday, string shortMonth, string monthYear)
    {
        using var _ = Formats(formats);

        Assert.Equal(weekday, DateText.Format(Thursday, DateText.Weekday));
        Assert.Equal(month, DateText.Format(Thursday, DateText.Month));
        Assert.Equal(shortWeekday, DateText.Format(Thursday, DateText.ShortWeekday));
        Assert.Equal(shortMonth, DateText.Format(Thursday, DateText.ShortMonth));
        Assert.Equal(monthYear, DateText.Format(Thursday, DateText.MonthYear));
    }

    [Fact]
    public void ADate_IsTheWallClocksDay_WhateverItsKind()
    {
        // A date is written as it reads: its own day and hour, never moved into another zone.
        using var _ = Formats("de");

        Assert.Equal("5.11.2026", DateText.Date(DateTime.SpecifyKind(new DateTime(2026, 11, 5, 23, 30, 0), DateTimeKind.Utc)));
        Assert.Equal("23:30", DateText.Time(DateTime.SpecifyKind(new DateTime(2026, 11, 5, 23, 30, 0), DateTimeKind.Local)));
    }

    [Fact]
    public void WindowsOwnClock_IsKept_WhenTheUserChoseOneOfTheOtherHours()
    {
        // An American format set to a 24-hour clock in Windows, as the web's 24h preference turns Intl's hour cycle.
        var american = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        american.DateTimeFormat.ShortTimePattern = "HH:mm";
        using var _ = AppText.Use(TextLanguage.Load("en", formats: FormatLocale.For(american)));

        Assert.Equal("15:04", DateText.Time(Thursday));
        Assert.Equal("09:04", DateText.Time(Thursday.AddHours(-6)));
        Assert.Equal("11/5/2026", DateText.Date(Thursday));
    }

    [Theory]
    [InlineData("de", DateText.NumericDate, "05.11.2026")]
    [InlineData("de", DateText.HourMinute, "15:04")]
    [InlineData("en", DateText.Hour, "3 PM")]
    [InlineData("ar", DateText.NumericDate, "٥‏‏/١١‏‏/٢٠٢٦")]
    public void WithoutICU_DotNetsNearestPattern_Writes(string formats, string skeleton, string expected)
    {
        // .NET's patterns are near Intl's, not the same: German pads its day, Arabic doubles its marks.
        Assert.Equal(expected, DateText.Fallback(Thursday, skeleton, FormatLocale.For(formats)));
    }

    private static IDisposable Formats(string tag) => AppText.Use(TextLanguage.Load("en", formats: FormatLocale.For(tag)));
}
