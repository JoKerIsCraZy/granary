using System.Text.RegularExpressions;
using Granary.Core.Diagnostics;

namespace Granary.Player;

/// <summary>Network failures that libmpv only reports as log text.</summary>
public enum MpvNetworkFailure
{
    None,
    Unauthorized,
    Forbidden,
    NotFound,
    Gone,
    OtherHttpError,
}

/// <summary>
/// libmpv's HTTP stream (libcurl) reports HTTP status codes only in log lines such as
/// <c>curl: HTTP error 401</c>. A 401 in the middle of a stream even surfaces as end of file, so the
/// engine watches the log to tell an expired token from a finished video.
/// </summary>
public static partial class MpvLogClassifier
{
    [GeneratedRegex(@"\bHTTP error (\d{3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex HttpError();

    [GeneratedRegex(@"(https?://[^\s?#]+)\?[^\s#]*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlQuery();

    // A Windows path up to a double quote or the line's end: a drive's, a share's (also written with forward slashes, bare
    // or as a file URL), or one in the extended form (\\?\). It does not stop at an apostrophe, which a folder's name can
    // hold; what follows the path on the line stays (WithoutFolder). A web address is none: its slashes follow a colon.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\|file://(?=[^/\s])|(?<![:/])//(?=[^/\s]))[^""\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPath();

    // A dot and a short extension (the group), wherever a path's last part has one.
    [GeneratedRegex(@"\.([A-Za-z0-9]{1,8})(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex Extension();

    // What mpv plays and reads: media, subtitles, fonts, and its own configuration and scripts. A last part that ends in
    // anything else is as likely a folder with a dot in its name, which can be the Windows user's ("elena.moreau").
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "mp4", "m4v", "mov", "avi", "webm", "ts", "m2ts", "mts", "mpg", "mpeg", "wmv", "flv", "ogv", "3gp", "part",
        "mka", "m4a", "mp3", "flac", "aac", "ac3", "eac3", "dts", "ogg", "opus", "wav",
        "srt", "ass", "ssa", "vtt", "sub", "idx", "sup", "smi",
        "ttf", "otf", "ttc", "conf", "lua", "js", "json", "glsl", "hook", "log",
    };

    public static MpvNetworkFailure Classify(string prefix, string text)
    {
        if (!prefix.Contains("curl", StringComparison.OrdinalIgnoreCase)
            && !prefix.Contains("stream", StringComparison.OrdinalIgnoreCase)
            && !prefix.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return MpvNetworkFailure.None;
        }
        var match = HttpError().Match(text);
        if (!match.Success)
        {
            return MpvNetworkFailure.None;
        }
        return match.Groups[1].Value switch
        {
            "401" => MpvNetworkFailure.Unauthorized,
            "403" => MpvNetworkFailure.Forbidden,
            "404" => MpvNetworkFailure.NotFound,
            "410" => MpvNetworkFailure.Gone,
            _ => MpvNetworkFailure.OtherHttpError,
        };
    }

    /// <summary>
    /// Never let a credential reach the app log, even if a verbose line echoes an option: a line that
    /// mentions an Authorization header is dropped whole, URLs lose their query (a plan's URL may carry a
    /// signature under any name), and everything else is masked. A local file keeps its name and loses its
    /// folder: a download's lies under the Windows user's own folder, which names them.
    /// </summary>
    public static string Redact(string text) =>
        text.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            ? "<redacted: line contained an Authorization header>"
            : LogRedactor.Redact(LocalPath().Replace(UrlQuery().Replace(text, "$1?…"), WithoutFolder));

    // "C:\Videos\clip.mkv" as "…\clip.mkv", with what followed it on the line. A path that ends in no name of a file mpv
    // would read is a folder, whose last part can be the Windows user's own, also with a dot in it: it goes whole. The name
    // is the one before the first such extension, and of a name with spaces only the last word stays: the words before it
    // can be a folder and what the line says after it ("C:\Users\elena failed, trying a.conf").
    private static string WithoutFolder(Match path)
    {
        string last = path.Value[path.Value.LastIndexOfAny(['\\', '/'])..];
        foreach (Match extension in Extension().Matches(last))
        {
            if (FileExtensions.Contains(extension.Groups[1].Value))
            {
                int space = last.LastIndexOfAny([' ', '\t'], extension.Index);
                return "…" + (space < 0 ? last : last[space..]);
            }
        }
        return "…";
    }
}
