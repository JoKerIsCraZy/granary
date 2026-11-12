using System.Globalization;
using System.Text.RegularExpressions;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Settings;

/// <summary>The icon a sign-in shows: what kind of device it came from.</summary>
public enum SessionDeviceKind
{
    Unknown,
    Desktop,
    Phone,
    Tv,
}

/// <summary>
/// How Settings → Devices words a sign-in and a device, as the web's <c>lib/loginSessionDevice.ts</c>,
/// <c>components/settings/DeviceList.tsx</c> and <c>components/admin/deviceOverrides.tsx</c> do.
/// </summary>
public static partial class DeviceText
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// The name and kind of a sign-in: a client that named itself keeps its name; a browser's User-Agent reads as
    /// "Chrome on Windows", in English as the web writes it, like the names the server gives devices; anything else that
    /// is no User-Agent is a name and shows as it is.
    /// </summary>
    public static (string Name, SessionDeviceKind Kind) SessionDevice(string? deviceName, string? platform)
    {
        string raw = deviceName?.Trim() ?? "";
        string reported = platform?.Trim() ?? "";
        if (reported.Length > 0)
        {
            string label = PlatformLabel(reported);
            return (raw.Length > 0 ? raw : UnknownDevice, KindOf(label.Length > 0 ? label : PlatformLabel(raw)));
        }
        if (raw.Length == 0)
        {
            return (UnknownDevice, SessionDeviceKind.Unknown);
        }
        string platformOf = PlatformLabel(raw);
        string app = AppOf(raw);
        if (app.Length == 0 && !UserAgentShape().IsMatch(raw))
        {
            return (raw, KindOf(platformOf));
        }
        string name = (app, platformOf) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{app} on {platformOf}",
            ({ Length: > 0 }, _) => app,
            (_, { Length: > 0 }) => $"Device on {platformOf}",
            _ => raw,
        };
        return (name, KindOf(platformOf));
    }

    /// <summary>A device's name when it reports none.</summary>
    public static string UnknownDevice => AppText.Get("web.settings.sessions.device.unknown");

    /// <summary>A sign-in seen within two minutes: "Active now", which the page shows in green.</summary>
    public static bool IsActiveNow(DateTimeOffset? lastSeen, DateTimeOffset now) => lastSeen is { } seen && now - seen < TimeSpan.FromMinutes(2);

    /// <summary>"Active now" within two minutes, then "Last seen 5 min ago", "3 hr ago", "2 days ago".</summary>
    public static string SessionLastSeen(DateTimeOffset? lastSeen, DateTimeOffset now)
    {
        if (lastSeen is not { } seen)
        {
            return AppText.Get("web.settings.sessions.lastSeen.never");
        }
        if (IsActiveNow(seen, now))
        {
            return AppText.Get("web.settings.sessions.lastSeen.active");
        }
        int minutes = (int)(now - seen).TotalMinutes;
        if (minutes < 60)
        {
            return AppText.Get("web.settings.sessions.lastSeen.minutes", ("count", minutes), ("count_text", AppText.Number(minutes)));
        }
        int hours = minutes / 60;
        if (hours < 24)
        {
            return AppText.Get("web.settings.sessions.lastSeen.hours", ("count", hours), ("count_text", AppText.Number(hours)));
        }
        int days = hours / 24;
        return AppText.Get("web.settings.sessions.lastSeen.days", ("count", days), ("count_text", AppText.Number(days)));
    }

    /// <summary>When a device was last used, in round figures: "Less than an hour ago", "Yesterday", "2 months ago".</summary>
    public static string DeviceLastSeen(DateTimeOffset? lastSeen, DateTimeOffset now)
    {
        if (lastSeen is not { } seen)
        {
            return AppText.Get("web.settings.deviceList.lastSeen.never");
        }
        int minutes = (int)Math.Max(0, Math.Round((now - seen).TotalMinutes, MidpointRounding.AwayFromZero));
        if (minutes < 60)
        {
            return AppText.Get("web.settings.deviceList.lastSeen.underAnHour");
        }
        int hours = Round(minutes / 60.0);
        if (hours < 24)
        {
            return AppText.Get("web.settings.deviceList.lastSeen.hours", ("count", hours), ("count_text", AppText.Number(hours)));
        }
        int days = Round(hours / 24.0);
        if (days == 1)
        {
            return AppText.Get("web.settings.deviceList.lastSeen.yesterday");
        }
        if (days < 30)
        {
            return AppText.Get("web.settings.deviceList.lastSeen.days", ("count", days), ("count_text", AppText.Number(days)));
        }
        int months = Round(days / 30.0);
        if (months < 12)
        {
            return AppText.Get("web.settings.deviceList.lastSeen.months", ("count", months), ("count_text", AppText.Number(months)));
        }
        int years = Round(months / 12.0);
        return AppText.Get("web.settings.deviceList.lastSeen.years", ("count", years), ("count_text", AppText.Number(years)));
    }

    /// <summary>
    /// What a device's reported platform is: TV, Tablet, Mobile, Desktop, or Other, as identifiers the page picks icons by;
    /// <see cref="KindLabel"/> names them.
    /// </summary>
    public static string PlatformKind(string? platform)
    {
        string p = platform?.ToLowerInvariant() ?? "";
        if (p.Length == 0)
        {
            return "Other";
        }
        if (ContainsAny(p, "tvos", "apple tv", "androidtv", "android tv", "roku", "firetv", "fire tv", "webos", "tizen") || TvWord().IsMatch(p))
        {
            return "TV";
        }
        if (ContainsAny(p, "ipad", "tablet"))
        {
            return "Tablet";
        }
        if (ContainsAny(p, "ios", "iphone", "android", "mobile", "phone"))
        {
            return "Mobile";
        }
        return ContainsAny(p, "mac", "win", "linux", "desktop", "chrome", "safari", "firefox", "edge", "web", "browser") ? "Desktop" : "Other";
    }

    /// <summary>A kind of <see cref="PlatformKind"/> in the language on screen, as the web's platformKindLabel.</summary>
    public static string KindLabel(string kind) => kind switch
    {
        "TV" => AppText.Get("web.common.devicePlatform.kind.tv"),
        "Tablet" => AppText.Get("web.common.devicePlatform.kind.tablet"),
        "Mobile" => AppText.Get("web.common.devicePlatform.kind.mobile"),
        "Desktop" => AppText.Get("web.common.devicePlatform.kind.desktop"),
        _ => AppText.Get("web.common.devicePlatform.kind.unknown"),
    };

    /// <summary>A date and time in the viewer's own zone, as the web writes it: "Oct 1, 2026, 8:00 AM".</summary>
    public static string Stamp(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("MMM d, yyyy, h:mm tt", English);

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static bool ContainsAny(string text, params string[] parts) => parts.Any(part => text.Contains(part, StringComparison.Ordinal));

    private static string PlatformLabel(string value) =>
        AppleTv().IsMatch(value) ? "Apple TV"
        : AndroidTv().IsMatch(value) ? "Android TV"
        : Has(value, "Android") ? "Android"
        : Has(value, "iPad") ? "iPad"
        : Has(value, "iPhone") ? "iPhone"
        : Has(value, "iOS") ? "iOS"
        : Has(value, "Windows") ? "Windows"
        : Mac().IsMatch(value) ? "macOS"
        : Has(value, "Linux") ? "Linux"
        : "";

    private static SessionDeviceKind KindOf(string platform) =>
        platform.Contains("TV", StringComparison.Ordinal) ? SessionDeviceKind.Tv
        : platform is "Android" or "iPhone" or "iPad" or "iOS" ? SessionDeviceKind.Phone
        : platform.Length > 0 ? SessionDeviceKind.Desktop
        : SessionDeviceKind.Unknown;

    private static string AppOf(string userAgent) =>
        SiloWord().IsMatch(userAgent) ? "Silo app"
        : Edge().IsMatch(userAgent) ? "Edge"
        : Firefox().IsMatch(userAgent) ? "Firefox"
        : Opera().IsMatch(userAgent) ? "Opera"
        : Chrome().IsMatch(userAgent) ? "Chrome"
        : userAgent.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
        : "";

    private static bool Has(string value, string word) => value.Contains(word, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("tvOS|Apple ?TV", RegexOptions.IgnoreCase)]
    private static partial Regex AppleTv();

    [GeneratedRegex("Android.*TV|Android TV", RegexOptions.IgnoreCase)]
    private static partial Regex AndroidTv();

    [GeneratedRegex("Macintosh|macOS|Mac OS X", RegexOptions.IgnoreCase)]
    private static partial Regex Mac();

    [GeneratedRegex(@"\bSilo\b", RegexOptions.IgnoreCase)]
    private static partial Regex SiloWord();

    [GeneratedRegex("Edg(?:e|A|iOS)?/")]
    private static partial Regex Edge();

    [GeneratedRegex("(?:Firefox|FxiOS)/")]
    private static partial Regex Firefox();

    [GeneratedRegex("(?:OPR|Opera)/")]
    private static partial Regex Opera();

    [GeneratedRegex("(?:Chrome|CriOS|HeadlessChrome)/")]
    private static partial Regex Chrome();

    // Something like "okhttp/4.12.0": a program and its version, so a User-Agent rather than a name someone chose.
    [GeneratedRegex(@"^[\w.-]+/\S")]
    private static partial Regex UserAgentShape();

    [GeneratedRegex(@"\btv\b")]
    private static partial Regex TvWord();
}
