using System.Text.Json;
using Granary.Core.Api.Catalog;

namespace Granary.Core.Api.Notifications;

/// <summary><c>GET /api/v2/notifications</c>: one page of the profile's inbox, newest first.</summary>
public sealed record NotificationPage
{
    public IReadOnlyList<NotificationItem> Items { get; init => field = value ?? []; } = [];

    public PageInfo? Page { get; init; }

    /// <summary>The validator Mark all read sends back as <c>through</c>: it marks what this listing saw, nothing newer.</summary>
    public string ReadCutoff { get; init => field = value ?? ""; } = "";
}

/// <summary>An inbox entry (docs/api-contract.md, 7.14).</summary>
public sealed record NotificationItem
{
    public string Id { get; init => field = value ?? ""; } = "";

    /// <summary>Open set, see <see cref="NotificationTypes"/>.</summary>
    public string Type { get; init => field = value ?? ""; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null while unread.</summary>
    public DateTimeOffset? ReadAt { get; init; }

    public string? SeriesId { get; init; }

    public string? SeriesTitle { get; init; }

    public string? EpisodeId { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public string? EpisodeTitle { get; init; }

    public string? PosterUrl { get; init; }

    /// <summary>Event-specific data whose keys depend on <see cref="Type"/>; unknown keys are ignored.</summary>
    public JsonElement ReasonFlags { get; init; }

    public bool IsUnread => ReadAt is null;

    /// <summary>A boolean flag of <see cref="ReasonFlags"/>, false when absent or of another kind.</summary>
    public bool Flag(string key) =>
        ReasonFlags.ValueKind == JsonValueKind.Object && ReasonFlags.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A text of <see cref="ReasonFlags"/>, null when absent or of another kind.</summary>
    public string? Text(string key) =>
        ReasonFlags.ValueKind == JsonValueKind.Object && ReasonFlags.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Known values of <see cref="NotificationItem.Type"/>; the set is open.</summary>
public static class NotificationTypes
{
    public const string EpisodeAvailable = "episode.available";

    public const string RequestFulfilled = "request.fulfilled";

    public const string RequestApproved = "request.approved";

    public const string RequestDeclined = "request.declined";
}

/// <summary><c>GET /api/v2/notifications/unread-count</c>.</summary>
public sealed record NotificationUnreadCount(int Count);

/// <summary><c>GET /api/v2/notifications/capabilities</c> (only what the inbox needs).</summary>
public sealed record NotificationCapabilities
{
    public NotificationInAppCapability? InApp { get; init; }

    public NotificationSeriesCapability? Series { get; init; }

    /// <summary>Whether the inbox is there: hidden when the administrator turned it off.</summary>
    public bool HasInbox => InApp?.Enabled ?? true;
}

public sealed record NotificationInAppCapability(bool Enabled);

/// <param name="Available">The Series view lists the series whose new episodes can notify.</param>
/// <param name="Mute">A series can be muted from it.</param>
public sealed record NotificationSeriesCapability(bool Available, bool Mute);

/// <summary><c>GET</c> and <c>PUT /api/v2/notifications/preferences</c>: the profile's switches.</summary>
public sealed record NotificationPreferences
{
    /// <summary>The master switch; the others only count while it is on.</summary>
    public bool Enabled { get; init; }

    public bool NotifyFavorites { get; init; }

    public bool NotifyWatchlist { get; init; }

    public bool NotifyContinueWatching { get; init; }

    public bool NotifyNextUp { get; init; }
}

/// <summary>The body of <c>POST /api/v2/notifications/read-all</c>.</summary>
public sealed record NotificationReadAll(string Through);
