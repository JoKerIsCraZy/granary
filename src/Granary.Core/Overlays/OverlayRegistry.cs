using System.Globalization;

namespace Granary.Core.Overlays;

/// <summary>A corner of a poster where badges stack.</summary>
public enum OverlayPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>The settings' groups of badges.</summary>
public enum OverlayCategory
{
    /// <summary>"Media Info".</summary>
    Tech,

    /// <summary>"Ratings &amp; Certifications".</summary>
    Ratings,

    /// <summary>"Content Metadata".</summary>
    Metadata,

    /// <summary>"Status &amp; Awards".</summary>
    Ribbons,
}

/// <summary>
/// What a card knows for its badges, as the web's <c>OverlayData</c> (<c>lib/overlays/types.ts</c>): the best file's
/// technical summary, the title's ratings and metadata, and for a watchlist title outside the library its request state.
/// </summary>
public sealed record OverlayData
{
    public string? Resolution { get; init; }

    public string? Hdr { get; init; }

    public string? Audio { get; init; }

    public string? AudioChannels { get; init; }

    public string? VideoCodec { get; init; }

    public string? Container { get; init; }

    public string? AspectRatio { get; init; }

    public string? ReleaseType { get; init; }

    public string? Edition { get; init; }

    public bool MultiAudio { get; init; }

    public bool MultiSub { get; init; }

    public double? RatingImdb { get; init; }

    public double? RatingTmdb { get; init; }

    public double? RatingRtCritic { get; init; }

    public double? RatingRtAudience { get; init; }

    public string? ContentRating { get; init; }

    public int? AdvisoryAge { get; init; }

    public int? Year { get; init; }

    /// <summary>Minutes.</summary>
    public int? Runtime { get; init; }

    public string? OriginalLanguage { get; init; }

    public string? Studio { get; init; }

    public string? Network { get; init; }

    public string? ShowStatus { get; init; }

    /// <summary>A watchlist title's request state: "Downloading 43%", "Out Dec 18".</summary>
    public string? RequestStatus { get; init; }

    public string? RequestStatusIcon { get; init; }

    /// <summary>TMDB lost the title as saved: the badge is amber.</summary>
    public bool RequestStatusAttention { get; init; }
}

/// <summary>One kind of badge, as the web's registry describes it (<c>lib/overlays/registry</c>).</summary>
/// <param name="IconId">The badge's fixed icon, when it has one (the web's icon ids: <c>monitor</c>, <c>volume</c>, …).</param>
/// <param name="DefaultAccent">The colour a badge starts in, "#f5c518" for IMDb.</param>
/// <param name="IconCapable">Whether the settings offer an icon for it.</param>
/// <param name="IntroducedInManifest">The settings manifest revision that first accepts the id; null for any.</param>
/// <param name="AvailabilityNote">Why the badge may show on few titles yet, under it in the settings.</param>
/// <param name="RatingSource">A rating an administrator can hide: offered only while the server shows it.</param>
public sealed record OverlayDef(
    string Id,
    OverlayCategory Category,
    string Label,
    string Description,
    OverlayPosition DefaultPosition,
    bool DefaultEnabled,
    Func<OverlayData, string?> GetValue,
    string? IconId = null,
    string? DefaultAccent = null,
    bool IconCapable = false,
    int? IntroducedInManifest = null,
    string? AvailabilityNote = null,
    string? RatingSource = null,
    Func<OverlayData, string?>? GetIcon = null,
    Func<OverlayData, string?>? GetAccent = null);

/// <summary>Every badge Granary knows, in the web's order; the settings list them so and the corners stack them so.</summary>
public static class OverlayRegistry
{
    /// <summary>The amber of a watchlist title TMDB lost.</summary>
    public const string AttentionAccent = "#f59e0b";

    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    public static IReadOnlyList<OverlayDef> All { get; } =
    [
        // Media Info
        new("resolution", OverlayCategory.Tech, "Resolution", "Video resolution (4K, 1080p, 720p, etc.)", OverlayPosition.TopLeft, true,
            d => PrettyResolution(d.Resolution), IconId: "monitor", IconCapable: true),
        new("hdr", OverlayCategory.Tech, "HDR / Dolby Vision", "Dynamic range format (HDR10, DV, HLG)", OverlayPosition.TopLeft, true,
            d => Blank(d.Hdr), IconCapable: true, GetIcon: d => HdrIcon(d.Hdr)),
        new("resolution_hdr", OverlayCategory.Tech, "Resolution + HDR (combined)",
            "Single badge combining resolution and dynamic range (e.g. \"4K DV\", \"1080p HDR\")", OverlayPosition.TopLeft, false,
            d => PrettyResolution(d.Resolution) is { } resolution
                ? CompactHdrSuffix(d.Hdr) is { } hdr ? $"{resolution} {hdr}" : resolution
                : null,
            IconCapable: true, GetIcon: d => d.Hdr?.Contains("DV", StringComparison.Ordinal) == true ? "dolby-vision" : null),
        new("audio", OverlayCategory.Tech, "Audio Codec", "Audio codec (Atmos, DTS-HD, TrueHD, etc.)", OverlayPosition.TopLeft, true,
            d => Blank(d.Audio), IconCapable: true, GetIcon: d => AudioIcon(d.Audio)),
        new("audio_channels", OverlayCategory.Tech, "Audio Channels", "Channel layout (Stereo, 5.1, 7.1)", OverlayPosition.TopLeft, false,
            d => Blank(d.AudioChannels), IconId: "volume", IconCapable: true),
        new("video_codec", OverlayCategory.Tech, "Video Codec", "Video codec (H.264, H.265, AV1)", OverlayPosition.TopLeft, false,
            d => Blank(d.VideoCodec), IconId: "film", IconCapable: true, GetIcon: d => VideoCodecIcon(d.VideoCodec)),
        new("container", OverlayCategory.Tech, "Container", "File container (MKV, MP4, etc.)", OverlayPosition.BottomLeft, false,
            d => Blank(d.Container)),
        new("aspect_ratio", OverlayCategory.Tech, "Aspect Ratio", "Display aspect ratio (16:9, 2.39:1, etc.)", OverlayPosition.BottomRight, false,
            d => Blank(d.AspectRatio), IconId: "layout", IconCapable: true),
        new("release_type", OverlayCategory.Tech, "Release Type", "Source format (REMUX, BluRay, WEB-DL, etc.)", OverlayPosition.BottomLeft, true,
            d => Blank(d.ReleaseType)),
        new("edition", OverlayCategory.Tech, "Edition", "Edition label from the best available media version", OverlayPosition.BottomLeft, false,
            d => Blank(d.Edition)),
        new("multi_audio", OverlayCategory.Tech, "Multi-Audio", "Shown when the file has audio in 2+ languages", OverlayPosition.BottomRight, false,
            d => d.MultiAudio ? "Multi-Audio" : null, IconId: "languages", IconCapable: true),
        new("multi_sub", OverlayCategory.Tech, "Subtitles Available", "Shown when the file has any subtitle track", OverlayPosition.BottomRight, false,
            d => d.MultiSub ? "CC" : null, IconId: "subtitles", IconCapable: true),

        // Ratings & Certifications
        new("rating_imdb", OverlayCategory.Ratings, "IMDb Rating", "IMDb score out of 10", OverlayPosition.TopRight, false,
            d => Rating("IMDb", d.RatingImdb, outOfHundred: false), DefaultAccent: "#f5c518"),
        new("rating_tmdb", OverlayCategory.Ratings, "TMDB Rating", "TMDB score out of 10", OverlayPosition.TopRight, false,
            d => Rating("TMDB", d.RatingTmdb, outOfHundred: false), DefaultAccent: "#01b4e4"),
        new("rating_rt", OverlayCategory.Ratings, "RT Critics", "Rotten Tomatoes critic score, when an administrator shows it",
            OverlayPosition.TopRight, false, d => Rating("RT", d.RatingRtCritic, outOfHundred: true), DefaultAccent: "#fa320a",
            RatingSource: "rt_critic"),
        new("rating_rt_audience", OverlayCategory.Ratings, "RT Audience", "Rotten Tomatoes audience score, when an administrator shows it",
            OverlayPosition.TopRight, false, d => Rating("RT Audience", d.RatingRtAudience, outOfHundred: true), DefaultAccent: "#fa6400",
            RatingSource: "rt_audience"),
        new("content_rating", OverlayCategory.Ratings, "Age Rating", "Content rating (PG-13, TV-MA, R, etc.)", OverlayPosition.BottomRight, false,
            d => Blank(d.ContentRating), IconId: "shield", IconCapable: true),
        new("advisory_age", OverlayCategory.Ratings, "Advisory Age", "Recommended minimum viewer age, such as Common Sense Media's 13+",
            OverlayPosition.BottomRight, false, d => d.AdvisoryAge is int age and > 0 ? $"{age.ToString(English)}+" : null,
            IconId: "users", IconCapable: true, IntroducedInManifest: 13),

        // Content Metadata
        new("year", OverlayCategory.Metadata, "Year", "Release year", OverlayPosition.BottomLeft, false,
            d => d.Year is int year and > 0 ? year.ToString(English) : null),
        new("runtime", OverlayCategory.Metadata, "Runtime", "Item runtime in hours and minutes", OverlayPosition.BottomLeft, false,
            d => Runtime(d.Runtime), IconId: "clock", IconCapable: true),
        new("original_language", OverlayCategory.Metadata, "Language", "Original language of the content", OverlayPosition.BottomLeft, false,
            d => LanguageName(d.OriginalLanguage), IconId: "globe", IconCapable: true),
        new("studio", OverlayCategory.Metadata, "Studio", "Primary production studio (movies)", OverlayPosition.BottomRight, false,
            d => Blank(d.Studio), IconId: "building", IconCapable: true),
        new("network", OverlayCategory.Metadata, "Network", "Primary network (series)", OverlayPosition.BottomRight, false,
            d => Blank(d.Network), IconId: "tv", IconCapable: true),

        // Status & Awards
        new("show_status", OverlayCategory.Ribbons, "Show Status", "Series lifecycle: Returning, Ended, Cancelled", OverlayPosition.TopRight, false,
            d => ShowStatus(d.ShowStatus), IconId: "tv", IconCapable: true,
            AvailabilityNote: "Populated by metadata plugins (TMDB/TVDB updates pending)"),
        new("request_status", OverlayCategory.Ribbons, "Request Status",
            "Where a watchlist title the library doesn't have yet stands: downloading, release date, awaiting approval",
            OverlayPosition.TopLeft, true, d => Blank(d.RequestStatus), IconCapable: true, IntroducedInManifest: 15,
            GetIcon: d => d.RequestStatusIcon, GetAccent: d => d.RequestStatusAttention ? AttentionAccent : null),
    ];

    /// <summary>
    /// Ids the settings contract accepts that no field backs yet: neither shown nor edited, but kept through a save, so
    /// another client's choice for them survives (the web's passthrough ids).
    /// </summary>
    public static IReadOnlyList<string> PassthroughIds { get; } = ["imdb_top_250", "rt_certified_fresh"];

    private static readonly Dictionary<string, OverlayDef> ById = All.ToDictionary(def => def.Id, StringComparer.Ordinal);

    public static OverlayDef? Find(string id) => ById.GetValueOrDefault(id);

    public static bool IsKnown(string id) => ById.ContainsKey(id) || PassthroughIds.Contains(id);

    /// <summary>"4K" for 2160p, UHD and 4K; "8K"; "1080p" as it is; anything else upper-cased. The badge upper-cases it all.</summary>
    public static string? PrettyResolution(string? value)
    {
        string v = value?.Trim().ToLowerInvariant() ?? "";
        return v switch
        {
            "" => null,
            "2160p" or "4k" or "uhd" => "4K",
            "4320p" or "8k" => "8K",
            _ when v.Length > 1 && v[^1] == 'p' && v[..^1].All(char.IsAsciiDigit) => v,
            _ => value!.ToUpperInvariant(),
        };
    }

    /// <summary>Any Dolby Vision is "DV", any other HDR "HDR".</summary>
    public static string? CompactHdrSuffix(string? value) =>
        string.IsNullOrEmpty(value) ? null : value.Contains("DV", StringComparison.Ordinal) ? "DV" : "HDR";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? HdrIcon(string? value) => value switch
    {
        null or "" => null,
        _ when value.Contains("DV", StringComparison.Ordinal) => "dolby-vision",
        "HDR10" => "hdr10",
        "HDR" => "hdr",
        _ => null,
    };

    private static string? AudioIcon(string? value) => string.IsNullOrEmpty(value) ? null
        : value.Equals("atmos", StringComparison.OrdinalIgnoreCase) ? "atmos" : "volume";

    private static string? VideoCodecIcon(string? value) => string.IsNullOrEmpty(value) ? null : value == "AV1" ? "av1" : "film";

    // "IMDb 7.5", "RT 92%".
    private static string? Rating(string mark, double? value, bool outOfHundred) => value is { } score
        ? outOfHundred
            ? $"{mark} {Math.Round(score, MidpointRounding.AwayFromZero).ToString(English)}%"
            : $"{mark} {(Math.Round(score * 10, MidpointRounding.AwayFromZero) / 10).ToString("0.0", English)}"
        : null;

    private static string? Runtime(int? minutes) => minutes is int m and > 0
        ? m >= 60 ? $"{m / 60}h {m % 60}m" : $"{m}m"
        : null;

    // The language's English name, as the web's Intl.DisplayNames gives it ("en" → "English"); the code when unknown.
    private static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }
        try
        {
            var culture = CultureInfo.GetCultureInfo(code.Trim());
            return culture.EnglishName.Split(" (")[0] is { Length: > 0 } name && !culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal)
                ? name
                : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private static string? ShowStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "returning" or "returning series" or "continuing" or "in_production" or "in production" => "Returning",
        "ended" => "Ended",
        "cancelled" or "canceled" => "Cancelled",
        "upcoming" or "planned" => "Upcoming",
        _ => value,
    };
}
