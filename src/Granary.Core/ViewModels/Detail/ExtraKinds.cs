using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// The words for the kinds of a title's videos, as the web's <c>lib/extraKinds.ts</c> has them: YouTube's trailers
/// (<see cref="DetailTrailer"/>) and the server's local extras (<see cref="DetailExtraGroup"/>) share them.
/// </summary>
public static class ExtraKinds
{
    /// <summary>The web's <c>extraKindLabel</c>, for one video: "Trailer", "Behind the Scenes", "Extra" for anything unknown.</summary>
    public static string Label(string? kind) => Key(kind) switch
    {
        "trailer" => AppText.Get("web.detail.extras.kind.trailer"),
        "teaser" => AppText.Get("web.detail.extras.kind.teaser"),
        "featurette" => AppText.Get("web.detail.extras.kind.featurette"),
        "clip" => AppText.Get("web.detail.extras.kind.clip"),
        "behind_the_scenes" => AppText.Get("web.detail.extras.kind.behindTheScenes"),
        "bloopers" => AppText.Get("web.detail.extras.kind.bloopers"),
        "deleted_scene" => AppText.Get("web.detail.extras.kind.deletedScene"),
        _ => AppText.Get("web.detail.extras.kind.otherKind"),
    };

    /// <summary>The web's <c>extraKindGroupLabel</c>, a group's heading: "Featurettes", "Deleted Scenes", "Other" for anything unknown.</summary>
    public static string GroupLabel(string? kind) => Key(kind) switch
    {
        "trailer" => AppText.Get("web.detail.extras.kindGroup.trailer"),
        "teaser" => AppText.Get("web.detail.extras.kindGroup.teaser"),
        "featurette" => AppText.Get("web.detail.extras.kindGroup.featurette"),
        "clip" => AppText.Get("web.detail.extras.kindGroup.clip"),
        "behind_the_scenes" => AppText.Get("web.detail.extras.kindGroup.behindTheScenes"),
        "bloopers" => AppText.Get("web.detail.extras.kindGroup.bloopers"),
        "deleted_scene" => AppText.Get("web.detail.extras.kindGroup.deletedScene"),
        _ => AppText.Get("web.detail.extras.kindGroup.otherKind"),
    };

    /// <summary>
    /// The kind as the server names it ("behind_the_scenes"), also from another spelling ("Behind the Scenes",
    /// "deleted-scene"); "other" for a kind the server's list does not have, as its <c>NormalizeExtraKind</c> folds them.
    /// </summary>
    public static string Key(string? kind) => kind?.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_') switch
    {
        "behindthescenes" => "behind_the_scenes",
        "deleted_scenes" => "deleted_scene",
        ("trailer" or "teaser" or "featurette" or "clip" or "behind_the_scenes" or "bloopers" or "deleted_scene") and string known => known,
        _ => "other",
    };
}
