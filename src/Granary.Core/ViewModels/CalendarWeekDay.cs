using System.Windows.Input;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>Where a day of the week shown stands against today: past days' cards are dimmed, today's column is tinted.</summary>
public enum CalendarDayTime
{
    Past,
    Today,
    Future,
}

/// <summary>How an airing's badge is drawn: PREMIERE filled with the accent, FINALE outlined in it, the others outlined plainly.</summary>
public enum CalendarBadgeKind
{
    None,

    /// <summary>A series' first episode (<c>series_premiere</c>).</summary>
    Premiere,

    /// <summary>A season's first episode (<c>season_premiere</c>).</summary>
    NewSeason,

    /// <summary>A season's or series' last episode (<c>finale</c>).</summary>
    Finale,

    /// <summary>A movie's release (<c>digital</c>, <c>physical</c>) or a badge the server added since.</summary>
    Release,
}

/// <summary>A day of the week grid: "THU" over "1", today's with TODAY, then its airings or "Nothing scheduled".</summary>
public sealed record CalendarWeekDay(DateOnly Date, CalendarDayTime When, IReadOnlyList<CalendarAiring> Airings)
{
    /// <summary>"THU", the column's head.</summary>
    public string Weekday => AppText.Upper(CalendarText.ShortDay(Date));

    /// <summary>"1": the day of the month, in Latin digits as the web's week strip writes it.</summary>
    public string DayNumber => Date.Day.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"Thursday, October 1st": a narrow window's section heading and the column's name for screen readers.</summary>
    public string Heading => CalendarText.DayHeading(Date);

    public string Badge => IsToday ? AppText.Upper(AppText.Get("web.calendar.today")) : "";

    public bool IsToday => When == CalendarDayTime.Today;

    public bool IsPast => When == CalendarDayTime.Past;

    public bool HasAirings => Airings.Count > 0;

    public bool HasNoAirings => Airings.Count == 0;
}

/// <summary>
/// A card of the week grid: the poster's middle as a 16:9 still with the air time and the badge on it, the series' or
/// movie's title and "S3 · E4" under it. It opens the series, or the movie itself.
/// </summary>
/// <param name="OpenCommand">Takes the airing.</param>
public sealed record CalendarAiring(string Id, string Title, ICommand OpenCommand)
{
    /// <summary>"S3 · E4", "Season 3" or "Movie".</summary>
    public string Code { get; init; } = "";

    /// <summary>"9:00 PM" on the viewer's clock, or null when the server knows no time.</summary>
    public string? Time { get; init; }

    /// <summary>"PREMIERE", "NEW SEASON", "FINALE" …, or null.</summary>
    public string? Badge { get; init; }

    public CalendarBadgeKind BadgeKind { get; init; }

    public bool IsWatched { get; init; }

    /// <summary>The airing's poster: the calendar carries no still or backdrop.</summary>
    public Uri? ImageUrl { get; init; }

    /// <summary>The poster's blur, shown until it is in.</summary>
    public string? ImageThumbhash { get; init; }

    /// <summary>"S2 · E1 - Hello" for the pointer, where the card shows only the code; null otherwise.</summary>
    public string? ToolTip { get; init; }

    /// <summary>"Severance, S2 · E1 - HELLO": the title and the episode in the web's capitals, as the calendar's cards were named.</summary>
    public string AccessibleName { get; init; } = "";

    public bool HasTime => !string.IsNullOrEmpty(Time);

    public bool HasBadge => !string.IsNullOrEmpty(Badge);

    public bool HasCode => Code.Length > 0;
}
