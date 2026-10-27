namespace Granary.Core.Api.Catalog;

/// <summary>
/// <c>GET /api/v2/catalog/items/{id}</c> for a movie, series, season or episode (docs/api-contract.md, 7.6):
/// the card members plus the detail page's own.
/// </summary>
public sealed record CatalogItemDetail : CatalogItemBase
{
    public string? OriginalTitle { get; init; }

    public string? Tagline { get; init; }

    public IReadOnlyList<string> Countries { get; init => field = value ?? []; } = [];

    public string? FirstAirDate { get; init; }

    /// <summary>An episode's air date.</summary>
    public string? AirDate { get; init; }

    public int? SeasonCount { get; init; }

    public int? EpisodeCount { get; init; }

    /// <summary>The external ratings in display order; render each as its name and display text.</summary>
    public IReadOnlyList<CatalogRating> Ratings { get; init => field = value ?? []; } = [];

    public IReadOnlyList<CastCredit> Cast { get; init => field = value ?? []; } = [];

    public IReadOnlyList<CrewCredit> Crew { get; init => field = value ?? []; } = [];

    public WatchRollup? UserData { get; init; }

    /// <summary>The season of <see cref="CatalogItemBase.PlayContentId"/> when that is an episode.</summary>
    public int? PlaySeasonNumber { get; init; }

    /// <summary>The playable files (movie, episode); empty for a series or season.</summary>
    public IReadOnlyList<FileVersion> Versions { get; init => field = value ?? []; } = [];

    /// <summary>Trailers and clips on external sites.</summary>
    public IReadOnlyList<ItemVideo> Videos { get; init => field = value ?? []; } = [];

    /// <summary>Local extras (behind the scenes, featurettes, …).</summary>
    public IReadOnlyList<ItemExtra> Extras { get; init => field = value ?? []; } = [];
}

/// <summary>An external rating; <see cref="Score"/> is on a 0-100 scale, <see cref="Display"/> on the source's own.</summary>
public sealed record CatalogRating(string Source, string Name, double Score, string Display);

/// <summary>
/// <c>GET /api/v2/capabilities/ratings</c>: the rating sources title pages and cards show, in display order (IMDb, TMDB and
/// those an administrator turned on). A card carries a Rotten Tomatoes score only while its source is listed.
/// </summary>
public sealed record RatingsCapability
{
    public string State { get; init => field = value ?? ""; } = "";

    public IReadOnlyList<RatingsCapabilitySource> Sources { get; init => field = value ?? []; } = [];

    /// <summary>The shown sources' names; none from a server that does not offer ratings.</summary>
    public IReadOnlySet<string> Shown => State == "available" ? Sources.Select(source => source.Source).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
}

public sealed record RatingsCapabilitySource(string Source, string Name);

public sealed record CastCredit(string Name, string Character, int Order, string? PersonId, string? PhotoUrl, string? PhotoThumbhash);

public sealed record CrewCredit(string Name, string Job, string? PersonId, string? PhotoUrl, string? PhotoThumbhash);

/// <summary>The viewer's watch state of an item, or summed over a series' or season's episodes.</summary>
public sealed record WatchRollup
{
    public bool Played { get; init; }

    public int WatchedCount { get; init; }

    public int UnplayedCount { get; init; }

    public int InProgressCount { get; init; }

    public bool? IsInProgress { get; init; }

    /// <summary>Resume position of the item or of the next episode.</summary>
    public double? PositionSeconds { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>The version last played.</summary>
    public string? LastFileId { get; init; }
}

/// <summary>One file of a movie or episode (only the members the detail page shows).</summary>
public sealed record FileVersion
{
    public string FileId { get; init => field = value ?? ""; } = "";

    public string? FileName { get; init; }

    public string Resolution { get; init => field = value ?? ""; } = "";

    public string CodecVideo { get; init => field = value ?? ""; } = "";

    public string CodecAudio { get; init => field = value ?? ""; } = "";

    public string Container { get; init => field = value ?? ""; } = "";

    public bool Hdr { get; init; }

    /// <summary>Bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>Seconds (not <c>duration_seconds</c> as on <c>/watch</c>, docs/api-contract.md 7.9).</summary>
    public long Duration { get; init; }

    /// <summary>Kilobits per second (docs/api-contract.md, 7.9).</summary>
    public long Bitrate { get; init; }

    public DateTimeOffset? AddedAt { get; init; }

    public string? EditionKey { get; init; }

    /// <summary>The edition as the file names it, for example <c>Director's Cut</c>.</summary>
    public string? EditionRaw { get; init; }
}

/// <summary>A trailer or clip hosted by <see cref="Site"/> (for example <c>YouTube</c>) under <see cref="SiteKey"/>; there is no URL.</summary>
public sealed record ItemVideo(string Kind, string Site, string SiteKey, bool IsOfficial, string? Name, string? Language);

/// <summary>A local extra. Its <see cref="FileId"/> is an integer on the wire, unlike other ids (docs/api-contract.md, 7.9).</summary>
public sealed record ItemExtra(string ContentId, string Kind, string? Title, long? DurationSeconds, long? FileId, string? StillUrl, string? StillThumbhash);
