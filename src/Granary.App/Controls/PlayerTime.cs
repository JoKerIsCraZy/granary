using System.Globalization;

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

    /// <summary>"1 hour 4 minutes 5 seconds"; zero parts are left out.</summary>
    public static string Spoken(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds)));
        var parts = new List<string>(3);
        AddPart(parts, (int)time.TotalHours, "hour");
        AddPart(parts, time.Minutes, "minute");
        AddPart(parts, time.Seconds, "second");
        return parts.Count == 0 ? "0 seconds" : string.Join(' ', parts);
    }

    private static void AddPart(List<string> parts, int value, string unit)
    {
        if (value > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{value} {unit}{(value == 1 ? "" : "s")}"));
        }
    }
}
