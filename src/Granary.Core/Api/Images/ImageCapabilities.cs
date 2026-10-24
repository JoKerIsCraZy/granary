namespace Granary.Core.Api.Images;

/// <summary>
/// <c>GET /api/v2/images/capabilities</c> (docs/api-contract.md, 7.10). Image size parameters may only be
/// sent while <see cref="State"/> is <c>available</c>; <see cref="ImageSizes.For"/> applies that rule.
/// </summary>
public sealed record ImageCapabilities
{
    public string State { get; init => field = value ?? ""; } = "";

    public bool Allowed { get; init; }

    public string Revision { get; init => field = value ?? ""; } = "";

    /// <summary>The size-class query parameter, <c>image_size</c> today.</summary>
    public string Param { get; init => field = value ?? ""; } = "";

    /// <summary>The size classes <see cref="Param"/> accepts: small, medium, large, original.</summary>
    public IReadOnlyList<string> Sizes { get; init => field = value ?? []; } = [];

    /// <summary>The width query parameter per slot (<see cref="ImageSlots"/>), for example poster → <c>poster_width</c>.</summary>
    public IReadOnlyDictionary<string, string> SlotWidthParams { get; init => field = value ?? new Dictionary<string, string>(); } = new Dictionary<string, string>();

    /// <summary>The pixel width of each size class per image type (poster, backdrop, logo, still, profile, …).</summary>
    public IReadOnlyDictionary<string, ImageSizeWidths> Widths { get; init => field = value ?? new Dictionary<string, ImageSizeWidths>(); } = new Dictionary<string, ImageSizeWidths>();

    public int OriginalMaxWidthPx { get; init; }

    /// <summary>The season-list query flag that leaves out poster URLs when false (<c>include_artwork</c>).</summary>
    public string SeasonListArtworkParam { get; init => field = value ?? ""; } = "";

    /// <summary>Movie and series detail may carry banner, thumb, clear-art and disc art.</summary>
    public bool ExtendedItemArtwork { get; init; }

    /// <summary>Cards may say whether an episode image is the episode's own still.</summary>
    public bool EpisodeStillProvenance { get; init; }

    /// <summary><c>local</c> or <c>s3</c>.</summary>
    public string? StorageBackend { get; init; }

    /// <summary><c>server</c> (root-relative artwork URLs) or <c>direct</c> (absolute storage URLs).</summary>
    public string? Delivery { get; init; }

    public bool IsAvailable => State == "available";
}

public sealed record ImageSizeWidths(int Small, int Medium, int Large);

/// <summary>The artwork slots the per-slot width parameters address (keys of <c>slot_width_params</c>).</summary>
public static class ImageSlots
{
    /// <summary><c>poster_url</c>.</summary>
    public const string Poster = "poster";

    /// <summary><c>backdrop_url</c> and <c>backdrop_urls</c>.</summary>
    public const string Backdrop = "backdrop";

    /// <summary><c>logo_url</c>.</summary>
    public const string Logo = "logo";

    /// <summary>An episode's or an extra's <c>still_url</c>.</summary>
    public const string Still = "still";

    /// <summary>A cast or crew <c>photo_url</c>.</summary>
    public const string Photo = "photo";
}

/// <summary>The values of <c>image_size</c>.</summary>
public static class ImageSizeClasses
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";
    public const string Original = "original";
}
