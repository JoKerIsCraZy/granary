namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// The moment the calendar's airings are judged at (docs/api-contract.md 7.13): the viewer's zone, the IANA name the calendar
/// is asked with, and today in the zone the server reckons its days in.
/// </summary>
/// <param name="Zone">The viewer's zone, which an airing's instant is shown in.</param>
/// <param name="TimeZone">The zone's IANA name; null when it has none, and the server reckons in UTC.</param>
/// <param name="Today">
/// The day in the zone the calendar is asked for, which its windows start from and its days without a time are listed in.
/// </param>
internal readonly record struct CalendarNow(DateTimeOffset Now, TimeZoneInfo Zone, string? TimeZone, DateOnly Today)
{
    /// <summary>The day in the viewer's zone, which an airing's instant is judged in; <see cref="Today"/> unless the server reckons in UTC.</summary>
    public DateOnly LocalToday => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, Zone).DateTime);

    /// <summary>The zone the server reckons the calendar's days in: the viewer's, or UTC for a zone without a name.</summary>
    public TimeZoneInfo DayZone => TimeZone is null ? TimeZoneInfo.Utc : Zone;

    /// <summary>The moment <paramref name="day"/> of the calendar starts, as Apple reads a date without a time.</summary>
    public DateTimeOffset StartOf(DateOnly day)
    {
        // A midnight a clock change skips gets the standard offset, which lands on the day's first moment after the change.
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, DayZone.GetUtcOffset(midnight));
    }

    public static CalendarNow Of(TimeProvider time)
    {
        var now = time.GetUtcNow();
        var zone = time.LocalTimeZone;
        string? timeZone = CalendarText.IanaZone(zone, now);
        // Without a zone name the server reckons the days in UTC, and so must the windows.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone is null ? TimeZoneInfo.Utc : zone).DateTime);
        return new(now, zone, timeZone, today);
    }
}
