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

    // A Windows path up to a quote or the line's end: a drive's, a share's, or one in the extended form (\\?\).
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\)[^'""\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPath();

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

    // "C:\Videos\clip.mkv" as "…\clip.mkv".
    private static string WithoutFolder(Match path)
    {
        int name = path.Value.LastIndexOfAny(['\\', '/']);
        return "…" + path.Value[name..];
    }
}
