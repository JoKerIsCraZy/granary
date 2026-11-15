using Granary.Core.Api.Notifications;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>A notified series worded as the web's <c>components/notifications/NotifiedSeriesList.tsx</c> words it.</summary>
internal static class NotifiedSeriesText
{
    /// <summary>The preference that lets each reason notify, named as its switch is.</summary>
    private static IReadOnlyList<(string Key, string Switch, Func<NotificationPreferences, bool> IsOn)> Switches =>
    [
        (NotifiedSeriesReasonKeys.Favorite, AppText.Get("web.notifications.preferences.favorites"), p => p.NotifyFavorites),
        (NotifiedSeriesReasonKeys.Watchlist, AppText.Get("web.notifications.preferences.watchlist"), p => p.NotifyWatchlist),
        (NotifiedSeriesReasonKeys.ContinueWatching, AppText.Get("web.notifications.preferences.continueWatching"), p => p.NotifyContinueWatching),
        (NotifiedSeriesReasonKeys.NextUp, AppText.Get("web.notifications.preferences.nextUp"), p => p.NotifyNextUp),
    ];

    /// <summary>What a new episode has to be for the series to notify: "Any new episode", "Any episode after S2E9".</summary>
    public static string Trigger(NotifiedSeries series)
    {
        var active = series.ActiveReasons;
        if (active.Favorite || active.Watchlist || active.ContinueWatching || series.NextUpAfter is not { } after)
        {
            return AppText.Get("web.notifications.series.anyNewEpisode");
        }
        string code = AppText.Get("web.notifications.queries.episodeCode", ("season", after.SeasonNumber), ("episode", after.EpisodeNumber));
        return AppText.Get("web.notifications.series.anyEpisodeAfter", ("code", code));
    }

    /// <summary>
    /// Why a listed series does not notify: the mute, the master switch, or the switches that silence its reasons
    /// ("Favorites and Next Up notifications are off"). Without the preferences only the mute is known.
    /// </summary>
    public static string Silenced(NotifiedSeries series, NotificationPreferences? preferences)
    {
        if (series.Muted)
        {
            return AppText.Get("web.notifications.series.muted");
        }
        if (preferences is { Enabled: false })
        {
            return AppText.Get("web.notifications.series.notificationsOff");
        }
        var off = preferences is null
            ? []
            : Switches.Where(s => series.Reasons.Has(s.Key) && !s.IsOn(preferences)).Select(s => s.Switch).ToList();
        if (off.Count == 0)
        {
            return AppText.Get("web.notifications.series.wontNotifyNow");
        }
        // The web's own list for these names: "Favorites, Watchlist and Next Up".
        string names = off.Count == 1
            ? off[0]
            : AppText.Get("web.notifications.series.nameList", ("names", string.Join(", ", off.Take(off.Count - 1))), ("last", off[^1]));
        return AppText.Get("web.notifications.series.reasonsOff", ("names", names));
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
    // As the web, which reads "(off)" after the pill's word.
    public string AccessibleName => IsOff ? $"{Label} {AppText.Get("web.notifications.series.reasonOff")}" : Label;
}
