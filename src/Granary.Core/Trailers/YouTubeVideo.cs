using System.Diagnostics.CodeAnalysis;

namespace Granary.Core.Trailers;

/// <summary>YouTube's addresses for a video, built from its key as the server names it (<see cref="Api.Catalog.ItemVideo.SiteKey"/>).</summary>
public static class YouTubeVideo
{
    /// <summary>YouTube's privacy-enhanced embed, which sets no cookies until the video plays; the web's TrailerModal uses it too.</summary>
    public const string EmbedHost = "www.youtube-nocookie.com";

    // YouTube video ids are 11 characters of [A-Za-z0-9_-]; the bound leaves room without admitting a URL.
    private const int MaximumKeyLength = 32;

    /// <summary>Whether <paramref name="key"/> can be a video's key: nothing that could change the address it goes into.</summary>
    public static bool IsKey([NotNullWhen(true)] string? key) =>
        key is { Length: > 0 and <= MaximumKeyLength } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The video's page on YouTube, for the browser.</summary>
    public static Uri WatchUrl(string key) => new("https://www.youtube.com/watch?v=" + Checked(key));

    /// <summary>YouTube's own thumbnail, as on the web; it needs no credentials.</summary>
    public static Uri ThumbnailUrl(string key) => new($"https://i.ytimg.com/vi/{Checked(key)}/hqdefault.jpg");

    /// <summary>
    /// The embedded player: it plays at once, suggests only the channel's own videos at its end, plays inline, and speaks
    /// the IFrame Player API to <paramref name="origin"/> alone.
    /// </summary>
    public static Uri EmbedUrl(string key, Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        string answers = Uri.EscapeDataString(origin.GetLeftPart(UriPartial.Authority));
        return new Uri($"https://{EmbedHost}/embed/{Checked(key)}?autoplay=1&rel=0&playsinline=1&enablejsapi=1&origin={answers}");
    }

    internal static string Checked(string key) => IsKey(key) ? key : throw new ArgumentException("Not a YouTube video key.", nameof(key));
}
