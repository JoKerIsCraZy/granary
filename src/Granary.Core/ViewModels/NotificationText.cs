using System.Globalization;
using Granary.Core.Api.Notifications;

namespace Granary.Core.ViewModels;

/// <summary>An inbox entry worded as the web's <c>pages/Notifications.tsx</c> words it.</summary>
internal static class NotificationText
{
    /// <summary>The reasons a profile hears about a series, keyed as <see cref="NotificationItem.ReasonFlags"/>, with their pills' labels.</summary>
    public static IReadOnlyList<(string Key, string Label)> Reasons { get; } =
        [("favorite", "Favorite"), ("watchlist", "Watchlist"), ("continue_watching", "Continue Watching"), ("next_up", "Next Up")];

    public static string Title(NotificationItem item) => item.Type switch
    {
        NotificationTypes.EpisodeAvailable => NonEmpty(item.SeriesTitle) ?? "New episode available",
        NotificationTypes.RequestFulfilled => NonEmpty(item.SeriesTitle) ?? "Request available",
        NotificationTypes.RequestApproved or NotificationTypes.RequestDeclined => NonEmpty(item.Text("title")) ?? "Media request",
        // The set of types is open: one this app does not know still shows.
        _ => "Notification",
    };

    public static string Description(NotificationItem item)
    {
        switch (item.Type)
        {
            case NotificationTypes.EpisodeAvailable:
                string? code = item.SeasonNumber is { } season && item.EpisodeNumber is { } episode ? $"S{season}E{episode}" : null;
                string text = string.Join(" — ", new[] { code, NonEmpty(item.EpisodeTitle) }.Where(part => part is not null));
                return text.Length > 0 ? text : "New episode available";
            case NotificationTypes.RequestFulfilled:
                string? kind = item.Text("media_type");
                return item.Flag("follower")
                    ? kind switch
                    {
                        "movie" => "A movie you followed is now available",
                        "series" => "A series you followed is now available",
                        _ => "A title you followed is now available",
                    }
                    : kind switch
                    {
                        "movie" => "Your requested movie is now available",
                        "series" => "Your requested series is now available",
                        _ => "Your request is now available",
                    };
            case NotificationTypes.RequestApproved:
                return "Your request was approved";
            case NotificationTypes.RequestDeclined:
                return NonEmpty(item.Text("reason")) is { } reason ? $"Your request was declined — {reason}" : "Your request was declined";
            default:
                return item.Type;
        }
    }

    /// <summary>"Just now", "5m ago", "3h ago", "2d ago", then "Oct 5".</summary>
    public static string When(DateTimeOffset createdAt, DateTimeOffset now)
    {
        int minutes = (int)Math.Round((now - createdAt).TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes < 1)
        {
            return "Just now";
        }
        if (minutes < 60)
        {
            return $"{minutes}m ago";
        }
        int hours = (int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero);
        if (hours < 24)
        {
            return $"{hours}h ago";
        }
        int days = (int)Math.Round(hours / 24.0, MidpointRounding.AwayFromZero);
        return days < 7 ? $"{days}d ago" : createdAt.ToString("MMM d", CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<string> ReasonLabels(NotificationItem item) =>
        [.. Reasons.Where(reason => item.Flag(reason.Key)).Select(reason => reason.Label)];

    private static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
