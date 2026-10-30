using Granary.Core.Api.Catalog;
using Granary.Core.Trailers;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// A trailer or clip on YouTube, played in Granary in YouTube's own player (<see cref="TrailerOverlayViewModel"/>). The API
/// names only the site and its key (<see cref="ItemVideo"/>); like the web client, Granary shows YouTube videos and skips
/// other sites.
/// </summary>
public sealed record DetailTrailer(string Title, string KindLabel, bool IsOfficial, string Key)
{
    /// <summary>The video's YouTube key, checked as the trailer is made: it goes into addresses and the trailer's page.</summary>
    public string Key { get; } = YouTubeVideo.Checked(Key);

    public string Caption => IsOfficial ? $"{KindLabel} · Official" : KindLabel;

    public string AccessibleName => $"{Title}, {Caption}, plays the trailer";

    /// <summary>The video's page on YouTube, for the browser when it can't play here.</summary>
    public Uri WatchUrl => YouTubeVideo.WatchUrl(Key);

    public Uri ThumbnailUrl => YouTubeVideo.ThumbnailUrl(Key);

    /// <summary>The playable videos in the server's order (trailers first), each YouTube key once.</summary>
    public static IReadOnlyList<DetailTrailer> From(IEnumerable<ItemVideo> videos) =>
        [.. videos.OfType<ItemVideo>().Select(From).OfType<DetailTrailer>().DistinctBy(t => t.Key)];

    /// <summary>The trailer for a YouTube video, or null for another site or a key that is not a YouTube id.</summary>
    public static DetailTrailer? From(ItemVideo video)
    {
        ArgumentNullException.ThrowIfNull(video);
        string key = video.SiteKey?.Trim() ?? "";
        if (!string.Equals(video.Site?.Trim(), "YouTube", StringComparison.OrdinalIgnoreCase) || !YouTubeVideo.IsKey(key))
        {
            return null;
        }
        string kind = KindText(video.Kind);
        return new DetailTrailer(string.IsNullOrWhiteSpace(video.Name) ? kind : video.Name.Trim(), kind, video.IsOfficial, key);
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
}
