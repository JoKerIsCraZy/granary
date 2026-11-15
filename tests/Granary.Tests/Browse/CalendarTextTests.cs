using Granary.Core.Texts;
using Granary.Core.ViewModels;

namespace Granary.Tests.Browse;

/// <summary>The calendar's dates, as the web's lib/calendarWeek.ts writes them in each language and format locale.</summary>
public sealed class CalendarTextTests
{
    private const char Fsi = '⁨';
    private const char Pdi = '⁩';

    private static readonly DateOnly Thursday = new(2026, 11, 5);

    [Theory]
    [InlineData("en", "Nov 2 – 8, 2026", "Sep 28 – Oct 4, 2026", "Thursday, November 5th")]
    [InlineData("de", "2.–8. Nov 2026", "28. Sept. – 4. Okt. 2026", "Donnerstag, 5. November")]
    [InlineData("fr", "2 – 8 nov. 2026", "28 sept. – 4 oct. 2026", "Jeudi 5 novembre")]
    [InlineData("ja", "2026年11月2日～8日", "9月28日～2026年10月4日", "11月5日（木曜日）")]
    public void TheWeekAndTheDay_AreInTheLanguage(string tag, string week, string weekAcrossMonths, string heading)
    {
        using var _ = AppText.Use(TextLanguage.Load(tag));

        Assert.Equal(week, CalendarText.WeekRange(new DateOnly(2026, 11, 2)));
        Assert.Equal(weekAcrossMonths, CalendarText.WeekRange(new DateOnly(2026, 9, 28)));
        Assert.Equal(heading, CalendarText.DayHeading(Thursday));
    }

    [Fact]
    public void InArabic_TheDayHeading_IsolatesItsNames_AndWritesArabicDigits()
    {
        using var _ = AppText.Use(TextLanguage.Load("ar"));

        Assert.Equal($"{Fsi}الخميس{Pdi}، ٥ {Fsi}نوفمبر{Pdi}", CalendarText.DayHeading(Thursday));
    }

    [Theory]
    [InlineData("en", "Thu", "Thu, Nov 5")]
    [InlineData("de", "Do", "Do., 5. Nov.")]
    public void TheShortDay_AndAnAiringsDate_AreTheFormatLocales(string tag, string shortDay, string shortDate)
    {
        using var _ = AppText.Use(TextLanguage.Load(tag));

        Assert.Equal(shortDay, CalendarText.ShortDay(Thursday));
        Assert.Equal(shortDate, CalendarText.ShortDate(Thursday));
    }
}
