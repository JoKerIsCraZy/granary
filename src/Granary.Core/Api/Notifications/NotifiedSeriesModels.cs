using Granary.Core.Api.Catalog;

namespace Granary.Core.Api.Notifications;

/// <summary><c>GET /api/v2/notifications/series</c>: a page of the series whose new episodes can notify the profile.</summary>
public sealed record NotifiedSeriesPage
{
    public IReadOnlyList<NotifiedSeries> Items { get; init => field = value ?? []; } = [];

    public PageInfo? Page { get; init; }
}

/// <summary>A series the profile hears about, and why (docs/api-contract.md, 7.14).</summary>
public sealed record NotifiedSeries
{
    /// <summary>The series as a catalog card.</summary>
    public CatalogItem? Item { get; init; }

    /// <summary>The profile's relationships to the series.</summary>
    public NotificationSeriesReasons Reasons { get; init => field = value ?? NotificationSeriesReasons.None; } = NotificationSeriesReasons.None;

    /// <summary>The reasons the profile's preferences let notify; all false when they silence the series.</summary>
    public NotificationSeriesReasons ActiveReasons { get; init => field = value ?? NotificationSeriesReasons.None; } = NotificationSeriesReasons.None;

    /// <summary>The profile muted the series: no new-episode notification, whatever the reasons.</summary>
    public bool Muted { get; init; }

    /// <summary>At least one reason is active and the series is not muted.</summary>
    public bool Notifies { get; init; }

    /// <summary>With Next Up active, the last episode the profile finished: on that reason alone, only a later one notifies.</summary>
    public NotificationEpisodeRef? NextUpAfter { get; init; }
}

public sealed record NotificationSeriesReasons(bool Favorite, bool Watchlist, bool ContinueWatching, bool NextUp)
{
    public static NotificationSeriesReasons None { get; } = new(false, false, false, false);

    /// <summary>A reason by its key, as <see cref="NotifiedSeriesReasonKeys"/> names them.</summary>
    public bool Has(string key) => key switch
    {
        NotifiedSeriesReasonKeys.Favorite => Favorite,
        NotifiedSeriesReasonKeys.Watchlist => Watchlist,
        NotifiedSeriesReasonKeys.ContinueWatching => ContinueWatching,
        NotifiedSeriesReasonKeys.NextUp => NextUp,
        _ => false,
    };
}

public sealed record NotificationEpisodeRef(int SeasonNumber, int EpisodeNumber);

/// <summary>The reasons' keys, as the series listing's <c>reason</c> filter and a notification's reason flags name them.</summary>
public static class NotifiedSeriesReasonKeys
{
    public const string Favorite = "favorite";

    public const string Watchlist = "watchlist";

    public const string ContinueWatching = "continue_watching";

    public const string NextUp = "next_up";
}

/// <summary>Which series a listing keeps.</summary>
public enum NotifiedSeriesStatus
{
    /// <summary>The series that notify.</summary>
    Active,

    /// <summary>The series that do not: the preferences silence every reason, or the profile muted them.</summary>
    Inactive,
}

/// <param name="Search">Keeps the series whose title contains it; empty keeps all.</param>
/// <param name="Reason">A key of <see cref="NotifiedSeriesReasonKeys"/>, or null for every reason.</param>
/// <param name="Sort">One field, descending with a leading '-': title, year, added_at, latest_episode_added.</param>
public sealed record NotifiedSeriesQuery(string Search, string? Reason, string Sort);
