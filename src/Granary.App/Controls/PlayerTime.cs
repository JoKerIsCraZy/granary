using System.Globalization;
using Granary.Core.Texts;

namespace Granary.App.Controls;

/// <summary>Times on the player as the web player writes them ("4:05", "1:04:05"), and spoken for screen readers.</summary>
public static class PlayerTime
{
    /// <summary>
    /// m:ss, or h:mm:ss from one hour on. With <paramref name="duration"/> of an hour or more, short times get
    /// the hours too, so position and duration line up while one of them is still under an hour.
    /// </summary>
    public static string Format(double seconds, double duration = 0)
    {
        var time = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds)));
        return time.TotalHours >= 1 || duration >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }

    /// <summary>The time left, as "-12:34".</summary>
    public static string Remaining(double position, double duration) =>
        "-" + Format(Math.Max(0, duration - Math.Max(0, position)), duration);

    /// <summary>
    /// "1 hour 4 minutes 5 seconds"; zero parts are left out. Minutes and seconds in the words of the web's seek bar
    /// (SeekBar.tsx, formatTimeLabel); the hours are Granary's, since the web counts on in minutes.
    /// </summary>
    public static string Spoken(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds)));
        int hours = (int)time.TotalHours;
        string? minutesText = time.Minutes > 0
            ? AppText.Get("web.player.seekBar.positionMinutes", ("minutes", time.Minutes), ("minutes_text", AppText.Number(time.Minutes)))
            : null;
        string? secondsText = time.Seconds > 0 || (hours == 0 && time.Minutes == 0)
            ? AppText.Get("web.player.seekBar.positionSeconds", ("seconds", time.Seconds))
            : null;
        string? rest = minutesText is not null && secondsText is not null
            ? AppText.Get("web.player.seekBar.positionMinutesSeconds", ("minutes", minutesText), ("seconds", secondsText))
            : minutesText ?? secondsText;
        if (hours == 0)
        {
            return rest ?? "";
        }
        string hoursText = AppText.Get("granary.player.spokenHours", ("hours", hours));
        return rest is null ? hoursText : AppText.Get("granary.player.spokenWithHours", ("hours", hoursText), ("rest", rest));
    }
}
