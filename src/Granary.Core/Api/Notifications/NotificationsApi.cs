using Granary.Core.Api.Catalog;

namespace Granary.Core.Api.Notifications;

/// <summary>The profile's in-app notifications (docs/api-contract.md, 7.14). Every call acts for the selected profile.</summary>
public sealed class NotificationsApi(SiloHttp http)
{
    /// <summary>Entries a page holds, as the web asks.</summary>
    public const int PageSize = 25;

    public Task<NotificationCapabilities> GetCapabilitiesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/notifications/capabilities", SiloAuth.Profile, SiloJsonContext.Default.NotificationCapabilities, cancellationToken);

    /// <summary>A page of the inbox, newest first; <paramref name="unreadOnly"/> leaves out what was read.</summary>
    public Task<NotificationPage> ListAsync(Uri server, bool unreadOnly, string? cursor = null, CancellationToken cancellationToken = default)
    {
        string query = new QueryBuilder().Add("status", unreadOnly ? "unread" : "all").Add("limit", PageSize).Add("cursor", cursor).ToString();
        return http.GetAsync(server, "api/v2/notifications" + query, SiloAuth.Profile, SiloJsonContext.Default.NotificationPage, cancellationToken);
    }

    public async Task<int> GetUnreadCountAsync(Uri server, CancellationToken cancellationToken = default) =>
        (await http.GetAsync(server, "api/v2/notifications/unread-count", SiloAuth.Profile, SiloJsonContext.Default.NotificationUnreadCount,
            cancellationToken).ConfigureAwait(false)).Count;

    public Task MarkReadAsync(Uri server, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id is "." or "..")
        {
            throw new ArgumentException($"'{id}' is not an id.", nameof(id));
        }
        return http.SendAsync(HttpMethod.Post, server, $"api/v2/notifications/{SiloHttp.Escape(id)}/read", SiloAuth.Profile, cancellationToken);
    }

    /// <summary>Marks everything a listing saw read; <paramref name="through"/> is its <see cref="NotificationPage.ReadCutoff"/>.</summary>
    public Task MarkAllReadAsync(Uri server, string through, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(through);
        return http.SendAsync(HttpMethod.Post, server, "api/v2/notifications/read-all", SiloAuth.Profile, new NotificationReadAll(through),
            SiloJsonContext.Default.NotificationReadAll, cancellationToken);
    }

    public Task<NotificationPreferences> GetPreferencesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/notifications/preferences", SiloAuth.Profile, SiloJsonContext.Default.NotificationPreferences, cancellationToken);

    /// <summary>Changes the switches set in <paramref name="change"/> and answers all of them.</summary>
    public Task<NotificationPreferences> UpdatePreferencesAsync(Uri server, NotificationPreferencesChange change, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Put, server, "api/v2/notifications/preferences", SiloAuth.Profile, change,
            SiloJsonContext.Default.NotificationPreferencesChange, SiloJsonContext.Default.NotificationPreferences, cancellationToken);
}

/// <summary>The body of <c>PUT /api/v2/notifications/preferences</c>: only the switches that change.</summary>
public sealed record NotificationPreferencesChange
{
    public bool? Enabled { get; init; }

    public bool? NotifyFavorites { get; init; }

    public bool? NotifyWatchlist { get; init; }

    public bool? NotifyContinueWatching { get; init; }

    public bool? NotifyNextUp { get; init; }
}
