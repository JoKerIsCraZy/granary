using System.Text.RegularExpressions;

namespace Granary.Core.Trailers;

/// <summary>
/// Where the trailer's WebView may go (TrailerOverlay asks before every navigation): the WebView itself only to Granary's
/// page, a frame in it only to YouTube's embedded player, and a link the player opens in a window of its own only to
/// YouTube, in the browser. Everything else is refused.
/// </summary>
/// <remarks>
/// Addresses are checked as the WebView hands them over, already in canonical form: https on the default port, without
/// userinfo, the host compared whole. Anything with a backslash, a space, a control or non-ASCII character is refused
/// rather than interpreted.
/// </remarks>
public static partial class TrailerWebPolicy
{
    private const int MaximumLength = 2048;

    private static readonly string[] EmbedHosts = [YouTubeVideo.EmbedHost, "www.youtube.com"];

    private static readonly string[] BrowserHosts = ["youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be"];

    /// <summary>The WebView goes to Granary's page and nowhere else.</summary>
    public static bool AllowsNavigation(string? url) =>
        Parse(url) is { } uri && IsHost(uri, TrailerPage.Host)
        && uri.AbsolutePath == TrailerPage.Url.AbsolutePath && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    /// <summary>A frame in it loads YouTube's embedded player, <c>/embed/&lt;key&gt;</c>, and nothing else.</summary>
    public static bool AllowsFrame(string? url) =>
        Parse(url) is { } uri && EmbedHosts.Any(host => IsHost(uri, host)) && EmbedPath().IsMatch(uri.AbsolutePath);

    /// <summary>A web message comes from Granary's own page: any other sender is not listened to.</summary>
    public static bool IsOwnOrigin(string? source) => Parse(source) is { } uri && IsHost(uri, TrailerPage.Host);

    /// <summary>
    /// What a link the player opens in a new window (its YouTube logo, the video's title) opens in the browser: YouTube's
    /// own pages; null for anything else, which is dropped. A new window never opens in Granary.
    /// </summary>
    public static Uri? BrowserTarget(string? url) => Parse(url) is { } uri && BrowserHosts.Any(host => IsHost(uri, host)) ? uri : null;

    private static Uri? Parse(string? url) =>
        url is { Length: > 0 and <= MaximumLength } && url.All(c => c is > ' ' and < '\u007F' and not '\\')
            && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
            && uri.UserInfo.Length == 0
            ? uri
            : null;

    private static bool IsHost(Uri uri, string host) => string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^/embed/[A-Za-z0-9_-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EmbedPath();
}
