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
        "trailer" => "Trailer",
        "teaser" => "Teaser",
        "featurette" => "Featurette",
        "clip" => "Clip",
        "behind_the_scenes" => "Behind the Scenes",
        "bloopers" => "Bloopers",
        "deleted_scene" => "Deleted Scene",
        _ => "Extra",
    };

    /// <summary>The web's <c>extraKindGroupLabel</c>, a group's heading: "Featurettes", "Deleted Scenes", "Other" for anything unknown.</summary>
    public static string GroupLabel(string? kind) => Key(kind) switch
    {
        "trailer" => "Trailers",
        "teaser" => "Teasers",
        "featurette" => "Featurettes",
        "clip" => "Clips",
        "behind_the_scenes" => "Behind the Scenes",
        "bloopers" => "Bloopers",
        "deleted_scene" => "Deleted Scenes",
        _ => "Other",
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
