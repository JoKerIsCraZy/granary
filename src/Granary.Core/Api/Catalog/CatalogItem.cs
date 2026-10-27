namespace Granary.Core.Api.Catalog;

/// <summary>
/// The members cards and the detail page share (docs/api-contract.md, 7.2). Image members are
/// presigned, short-lived and may be root-relative: resolve them with
/// <see cref="Images.ArtworkUrl.Resolve"/>. Dates are <c>YYYY-MM-DD</c> strings as the server stores them.
/// </summary>
public abstract record CatalogItemBase
{
    public string ContentId { get; init => field = value ?? ""; } = "";

    /// <summary>Open set, see <see cref="CatalogItemTypes"/>.</summary>
    public string Type { get; init => field = value ?? ""; } = "";

    public string Title { get; init => field = value ?? ""; } = "";

    /// <summary>Metadata match state (<c>matched</c>, …).</summary>
    public string Status { get; init => field = value ?? ""; } = "";

    public IReadOnlyList<string> Genres { get; init => field = value ?? []; } = [];

    /// <summary>The owning series of an episode or season.</summary>
    public string? SeriesId { get; init; }

    public string? SeriesTitle { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public int? Year { get; init; }

    public string? ReleaseDate { get; init; }

    public string? LastAirDate { get; init; }

    /// <summary>Minutes.</summary>
    public int? Runtime { get; init; }

    public string? ContentRating { get; init; }

    public string? Overview { get; init; }

    public string? PosterUrl { get; init; }

    public string? PosterThumbhash { get; init; }

    public string? BackdropUrl { get; init; }

    public string? BackdropThumbhash { get; init; }

    /// <summary>There is no logo thumbhash.</summary>
    public string? LogoUrl { get; init; }

    /// <summary>Resume position on a Continue Watching card.</summary>
    public double? PositionSeconds { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>Present on in-progress cards; it anchors a Continue Watching dismissal (<see cref="HomeDismissal"/>).</summary>
    public DateTimeOffset? ProgressUpdatedAt { get; init; }

    /// <summary>On a Continue Watching card, see <see cref="ItemSources"/>.</summary>
    public string? ItemSource { get; init; }

    /// <summary>The episode to play for a series or season card; absent when the item plays itself.</summary>
    public string? PlayContentId { get; init; }

    /// <summary>The viewer's flags; absent without a profile.</summary>
    public CatalogItemUserState? UserState { get; init; }

    /// <summary>Technical badges of the best file.</summary>
    public CatalogItemOverlay? OverlaySummary { get; init; }

    /// <summary>Section-specific badges (new, returning, …).</summary>
    public IReadOnlyList<string> Badges { get; init => field = value ?? []; } = [];

    public double? RatingImdb { get; init; }

    public double? RatingTmdb { get; init; }

    /// <summary>Rotten Tomatoes' critics and audience, out of 100, when an administrator shows them.</summary>
    public double? RatingRtCritic { get; init; }

    public double? RatingRtAudience { get; init; }

    /// <summary>A recommended minimum age, such as Common Sense Media's 13.</summary>
    public int? AdvisoryAge { get; init; }

    /// <summary>"en", "ja".</summary>
    public string? OriginalLanguage { get; init; }

    /// <summary>Airing state of a series.</summary>
    public string? ShowStatus { get; init; }

    public DateTimeOffset? AddedAt { get; init; }

    public IReadOnlyList<string> Studios { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> Networks { get; init => field = value ?? []; } = [];
}

/// <summary>A browsing card: Home and library rows, catalog listings, "More like this".</summary>
public sealed record CatalogItem : CatalogItemBase;

public sealed record CatalogItemUserState(bool Played, bool IsFavorite, bool InWatchlist, bool NotInterested);

/// <summary>Display strings for a card's technical badges, for example <c>4K</c>, <c>Dolby Vision</c>, <c>7.1</c>.</summary>
public sealed record CatalogItemOverlay
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

    /// <summary>Two or more audio languages.</summary>
    public bool MultiAudio { get; init; }

    /// <summary>At least one subtitle track.</summary>
    public bool MultiSub { get; init; }
}

/// <summary>A bounded or cursor-paged list of cards (<c>CatalogItemCollection</c>).</summary>
public sealed record CatalogItemCollection(IReadOnlyList<CatalogItem> Items, PageInfo? Page);

/// <summary>Cursor state of a paged collection; absent on bounded lists.</summary>
public sealed record PageInfo(bool HasMore, string? NextCursor);

/// <summary>Known values of <see cref="CatalogItemBase.Type"/>; the set is open.</summary>
public static class CatalogItemTypes
{
    public const string Movie = "movie";
    public const string Series = "series";
    public const string Season = "season";
    public const string Episode = "episode";
    public const string Audiobook = "audiobook";
    public const string Ebook = "ebook";
    public const string Podcast = "podcast";
    public const string PodcastEpisode = "podcast_episode";
}

/// <summary>Values of <see cref="CatalogItemBase.ItemSource"/> on a Continue Watching card.</summary>
public static class ItemSources
{
    public const string InProgress = "in_progress";
    public const string NextUp = "next_up";
}
