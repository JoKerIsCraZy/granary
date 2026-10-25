namespace Granary.Core.Api.Catalog;

/// <summary><c>GET /api/v2/calendar</c>: the days of the window that have airings, in order.</summary>
public sealed record CalendarWeek
{
    public IReadOnlyList<CalendarDay> Events { get; init => field = value ?? []; } = [];
}

public sealed record CalendarDay
{
    /// <summary>YYYY-MM-DD in the viewer's zone.</summary>
    public string Date { get; init => field = value ?? ""; } = "";

    public IReadOnlyList<CalendarEvent> Items { get; init => field = value ?? []; } = [];
}

/// <summary>A movie release or an episode airing.</summary>
public sealed record CalendarEvent
{
    /// <summary>The episode's or movie's id; the episode may not be in a library yet.</summary>
    public string ContentId { get; init => field = value ?? ""; } = "";

    /// <summary><c>movie</c>, <c>episode</c> or <c>season_premiere</c>.</summary>
    public string Type { get; init => field = value ?? ""; } = "";

    /// <summary>The movie's or series' title.</summary>
    public string Title { get; init => field = value ?? ""; } = "";

    public string? SeriesId { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public string? EpisodeTitle { get; init; }

    /// <summary>The airing as an instant, when the network's time and zone are known.</summary>
    public DateTimeOffset? AirAt { get; init; }

    /// <summary>Wall-clock HH:MM in the network's zone.</summary>
    public string? AirTime { get; init; }

    /// <summary><c>series_premiere</c>, <c>season_premiere</c>, <c>finale</c>.</summary>
    public IReadOnlyList<string> Badges { get; init => field = value ?? []; } = [];

    public string? PosterUrl { get; init; }

    public bool Watched { get; init; }
}

/// <summary>Values of the calendar's <c>filter</c>.</summary>
public static class CalendarFilters
{
    /// <summary>Shows the profile follows (the web's default).</summary>
    public const string Following = "following";

    public const string Trending = "trending";

    public const string Everything = "everything";
}
