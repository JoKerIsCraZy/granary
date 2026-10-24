namespace Granary.Core.Api.Catalog;

/// <summary><c>GET /api/v2/catalog/series/{id}/seasons</c>.</summary>
public sealed record SeasonCollection(IReadOnlyList<Season> Items, PageInfo? Page);

/// <summary>A season of a series (docs/api-contract.md, 7.7). Season 0 holds the specials.</summary>
public sealed record Season
{
    public string ContentId { get; init => field = value ?? ""; } = "";

    public int SeasonNumber { get; init; }

    public string Title { get; init => field = value ?? ""; } = "";

    public int EpisodeCount { get; init; }

    public bool? IsSpecials { get; init; }

    public string? AirDate { get; init; }

    public string? Overview { get; init; }

    /// <summary>The episode to play next.</summary>
    public string? PlayContentId { get; init; }

    public string? PosterUrl { get; init; }

    public string? PosterThumbhash { get; init; }

    public WatchRollup? UserData { get; init; }
}

/// <summary>The episodes of a season.</summary>
public sealed record EpisodeCollection(IReadOnlyList<Episode> Items, PageInfo? Page);

public sealed record Episode
{
    public string ContentId { get; init => field = value ?? ""; } = "";

    public int SeasonNumber { get; init; }

    public int EpisodeNumber { get; init; }

    public string Title { get; init => field = value ?? ""; } = "";

    /// <summary>Minutes.</summary>
    public int Runtime { get; init; }

    public string? Overview { get; init; }

    public string? AirDate { get; init; }

    public string? StillUrl { get; init; }

    public string? StillThumbhash { get; init; }

    public WatchRollup? UserData { get; init; }

    public CatalogItemOverlay? OverlaySummary { get; init; }

    /// <summary>The episode's IMDb and TMDB ratings; empty when it has none.</summary>
    public IReadOnlyList<CatalogRating> Ratings { get; init => field = value ?? []; } = [];

    public IReadOnlyList<EpisodeFile> Files { get; init => field = value ?? []; } = [];
}

public sealed record EpisodeFile
{
    public string FileId { get; init => field = value ?? ""; } = "";

    public string? Resolution { get; init; }

    public bool Hdr { get; init; }

    /// <summary>Bytes.</summary>
    public long FileSize { get; init; }

    public string? CodecVideo { get; init; }

    public string? Container { get; init; }

    public int? AudioChannels { get; init; }

    /// <summary>The server could not read the file; playback falls back to another version.</summary>
    public bool Unreadable { get; init; }
}
