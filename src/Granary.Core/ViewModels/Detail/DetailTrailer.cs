using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// A trailer or clip on YouTube, opened in the browser. The API names only the site and its key
/// (<see cref="ItemVideo"/>); like the web client, Granary shows YouTube videos and skips other sites.
/// </summary>
public sealed record DetailTrailer(string Title, string KindLabel, bool IsOfficial, Uri WatchUrl, Uri ThumbnailUrl)
{
    // YouTube video ids are 11 characters of [A-Za-z0-9_-]; the bound leaves room without admitting a URL.
    private const int MaximumKeyLength = 32;

    public string Caption => IsOfficial ? $"{KindLabel} · Official" : KindLabel;

    public string AccessibleName => $"{Title}, {Caption}, opens YouTube";

    /// <summary>The playable videos in the server's order (trailers first), each YouTube key once.</summary>
    public static IReadOnlyList<DetailTrailer> From(IEnumerable<ItemVideo> videos) =>
        [.. videos.Select(From).OfType<DetailTrailer>().DistinctBy(t => t.WatchUrl)];

    /// <summary>The trailer for a YouTube video, or null for another site or a key that is not a YouTube id.</summary>
    public static DetailTrailer? From(ItemVideo video)
    {
        ArgumentNullException.ThrowIfNull(video);
        string key = video.SiteKey?.Trim() ?? "";
        if (!string.Equals(video.Site?.Trim(), "YouTube", StringComparison.OrdinalIgnoreCase) || !IsYouTubeKey(key))
        {
            return null;
        }
        string kind = KindText(video.Kind);
        return new DetailTrailer(
            string.IsNullOrWhiteSpace(video.Name) ? kind : video.Name.Trim(),
            kind,
            video.IsOfficial,
            new Uri("https://www.youtube.com/watch?v=" + key),
            new Uri($"https://i.ytimg.com/vi/{key}/hqdefault.jpg"));
    }

    /// <summary>The web's extraKindLabel: "Trailer", "Behind the Scenes", "Extra" for anything unknown.</summary>
    public static string KindText(string? kind) => kind?.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_') switch
    {
        "trailer" => "Trailer",
        "teaser" => "Teaser",
        "featurette" => "Featurette",
        "clip" => "Clip",
        "behind_the_scenes" or "behindthescenes" => "Behind the Scenes",
        "bloopers" => "Bloopers",
        "deleted_scene" or "deleted_scenes" => "Deleted Scene",
        _ => "Extra",
    };

    private static bool IsYouTubeKey(string key) =>
        key.Length is > 0 and <= MaximumKeyLength && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
