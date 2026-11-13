using System.Globalization;
using Granary.Core.Api.Notifications;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>An inbox entry worded as the web's <c>pages/Notifications.tsx</c> words it.</summary>
internal static class NotificationText
{
    /// <summary>The reasons a profile hears about a series, keyed as <see cref="NotificationItem.ReasonFlags"/>, with their pills' labels.</summary>
    public static IReadOnlyList<(string Key, string Label)> Reasons =>
    [
        ("favorite", AppText.Get("web.notifications.reason.favorite")),
        ("watchlist", AppText.Get("web.notifications.reason.watchlist")),
        ("continue_watching", AppText.Get("web.notifications.reason.continueWatching")),
        ("next_up", AppText.Get("web.notifications.reason.nextUp")),
    ];

    public static string Title(NotificationItem item) => item.Type switch
    {
        NotificationTypes.EpisodeAvailable => NonEmpty(item.SeriesTitle) ?? AppText.Get("web.notifications.inbox.newEpisode"),
        NotificationTypes.RequestFulfilled => NonEmpty(item.SeriesTitle) ?? AppText.Get("web.notifications.inbox.requestAvailable"),
        NotificationTypes.RequestApproved or NotificationTypes.RequestDeclined => NonEmpty(item.Text("title")) ?? AppText.Get("web.notifications.inbox.mediaRequest"),
        // The set of types is open: one this app does not know still shows.
        _ => AppText.Get("web.notifications.inbox.genericTitle"),
    };

    public static string Description(NotificationItem item)
    {
        switch (item.Type)
        {
            case NotificationTypes.EpisodeAvailable:
                string? code = item.SeasonNumber is { } season && item.EpisodeNumber is { } episode
                    ? AppText.Get("web.notifications.queries.episodeCode", ("season", season), ("episode", episode))
                    : null;
                string? title = NonEmpty(item.EpisodeTitle);
                return code is not null && title is not null ? AppText.Get("web.notifications.inbox.episodeLine", ("code", code), ("title", title))
                    : code ?? title ?? AppText.Get("web.notifications.inbox.newEpisode");
            case NotificationTypes.RequestFulfilled:
                string? kind = item.Text("media_type");
                return item.Flag("follower")
                    ? kind switch
                    {
                        "movie" => AppText.Get("web.notifications.inbox.followedMovieAvailable"),
                        "series" => AppText.Get("web.notifications.inbox.followedSeriesAvailable"),
                        _ => AppText.Get("web.notifications.inbox.followedTitleAvailable"),
                    }
                    : kind switch
                    {
                        "movie" => AppText.Get("web.notifications.inbox.requestedMovieAvailable"),
                        "series" => AppText.Get("web.notifications.inbox.requestedSeriesAvailable"),
                        _ => AppText.Get("web.notifications.inbox.requestAvailableBody"),
                    };
            case NotificationTypes.RequestApproved:
                return AppText.Get("web.notifications.inbox.requestApproved");
            case NotificationTypes.RequestDeclined:
                return NonEmpty(item.Text("reason")) is { } reason
                    ? AppText.Get("web.notifications.inbox.requestDeclinedReason", ("reason", reason))
                    : AppText.Get("web.notifications.inbox.requestDeclined");
            default:
                return item.Type;
        }
    }

    /// <summary>"Just now", "5m ago", "3h ago", "2d ago", then "Oct 5" (a date, still in English).</summary>
    public static string When(DateTimeOffset createdAt, DateTimeOffset now)
    {
        int minutes = (int)Math.Round((now - createdAt).TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes < 1)
        {
            return AppText.Get("web.notifications.inbox.justNow");
        }
        if (minutes < 60)
        {
            return AppText.Get("web.notifications.inbox.minutesAgo", ("minutes", minutes));
        }
        int hours = (int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero);
        if (hours < 24)
        {
            return AppText.Get("web.notifications.inbox.hoursAgo", ("hours", hours));
        }
        int days = (int)Math.Round(hours / 24.0, MidpointRounding.AwayFromZero);
        return days < 7 ? AppText.Get("web.notifications.inbox.daysAgo", ("days", days)) : createdAt.ToString("MMM d", CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<string> ReasonLabels(NotificationItem item) =>
        [.. Reasons.Where(reason => item.Flag(reason.Key)).Select(reason => reason.Label)];

    private static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
