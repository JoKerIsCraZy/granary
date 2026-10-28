using Granary.Core.Api.Notifications;

namespace Granary.Core.ViewModels;

/// <summary>A notified series worded as the web's <c>components/notifications/NotifiedSeriesList.tsx</c> words it.</summary>
internal static class NotifiedSeriesText
{
    /// <summary>The preference that lets each reason notify, named as its switch is.</summary>
    private static readonly IReadOnlyList<(string Key, string Switch, Func<NotificationPreferences, bool> IsOn)> Switches =
    [
        (NotifiedSeriesReasonKeys.Favorite, "Favorites", p => p.NotifyFavorites),
        (NotifiedSeriesReasonKeys.Watchlist, "Watchlist", p => p.NotifyWatchlist),
        (NotifiedSeriesReasonKeys.ContinueWatching, "Continue Watching", p => p.NotifyContinueWatching),
        (NotifiedSeriesReasonKeys.NextUp, "Next Up", p => p.NotifyNextUp),
    ];

    /// <summary>What a new episode has to be for the series to notify: "Any new episode", "Any episode after S2E9".</summary>
    public static string Trigger(NotifiedSeries series)
    {
        var active = series.ActiveReasons;
        if (active.Favorite || active.Watchlist || active.ContinueWatching || series.NextUpAfter is not { } after)
        {
            return "Any new episode";
        }
        return $"Any episode after S{after.SeasonNumber}E{after.EpisodeNumber}";
    }

    /// <summary>
    /// Why a listed series does not notify: the mute, the master switch, or the switches that silence its reasons
    /// ("Favorites and Next Up notifications are off"). Without the preferences only the mute is known.
    /// </summary>
    public static string Silenced(NotifiedSeries series, NotificationPreferences? preferences)
    {
        if (series.Muted)
        {
            return "Muted";
        }
        if (preferences is { Enabled: false })
        {
            return "Notifications are off";
        }
        var off = preferences is null
            ? []
            : Switches.Where(s => series.Reasons.Has(s.Key) && !s.IsOn(preferences)).Select(s => s.Switch).ToList();
        if (off.Count == 0)
        {
            return "Won’t notify right now";
        }
        string names = off.Count == 1 ? off[0] : $"{string.Join(", ", off.Take(off.Count - 1))} and {off[^1]}";
        return $"{names} notifications are off";
    }

    /// <summary>The series' reasons as pills; one that does not count now (muted, or its switch off) is struck through.</summary>
    public static IReadOnlyList<ReasonPill> Pills(NotifiedSeries series) =>
    [
        .. NotificationText.Reasons
            .Where(reason => series.Reasons.Has(reason.Key))
            .Select(reason => new ReasonPill(reason.Label, series.Muted || !series.ActiveReasons.Has(reason.Key))),
    ];
}

/// <summary>A reason pill: "Favorite", struck through when it does not notify now.</summary>
public sealed record ReasonPill(string Label, bool IsOff)
{
    public string AccessibleName => IsOff ? $"{Label} (off)" : Label;
}
