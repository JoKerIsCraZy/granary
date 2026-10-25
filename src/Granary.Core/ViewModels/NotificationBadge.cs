using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Notifications;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// The sidebar's Notifications entry: whether the server has an inbox for the profile (the web shows it unless the
/// capability says otherwise) and how many entries are unread, as a red count beside it ("99+" past 99). It reads both
/// again for another profile, and the count again when asked after at least <see cref="FreshFor"/>, as the web does.
/// </summary>
public sealed partial class NotificationBadge : ObservableObject
{
    /// <summary>How long a count is taken as current.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    private readonly ISessionManager _session;
    private readonly NotificationsApi _notifications;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationBadge> _logger;
    private (string? ServerId, string? ProfileId)? _shownFor;
    private (string? ServerId, string? ProfileId)? _capabilityFor;
    private DateTimeOffset _countedAt = DateTimeOffset.MinValue;
    private int _reads;

    public NotificationBadge(ISessionManager session, NotificationsApi notifications, TimeProvider time, ILogger<NotificationBadge> logger)
    {
        _session = session;
        _notifications = notifications;
        _time = time;
        _logger = logger;
    }

    /// <summary>The inbox is there; false once the server said it is off or has none.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(CountText))]
    public partial int UnreadCount { get; private set; }

    public bool HasUnread => UnreadCount > 0;

    /// <summary>"3", or "99+".</summary>
    public string CountText => UnreadCount > 99 ? "99+" : UnreadCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads what is stale: the capability and the count for another profile, the count when older than
    /// <see cref="FreshFor"/> or when <paramref name="force"/>. A count that fails leaves the last one; a capability that
    /// fails hides the entry until a later call reads it.
    /// </summary>
    public async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (_session is not { State: SessionState.Ready, ActiveServer: { } server })
        {
            _shownFor = null;
            UnreadCount = 0;
            return;
        }
        var key = (_session.ActiveServer?.Id, _session.ProfileId);
        if (_shownFor != key)
        {
            _shownFor = key;
            UnreadCount = 0;
            _countedAt = DateTimeOffset.MinValue;
        }
        else if (_capabilityFor == key && !force && _time.GetUtcNow() - _countedAt < FreshFor)
        {
            return;
        }
        if (_capabilityFor != key)
        {
            bool? available = await InboxAvailableAsync(server.BaseUri, cancellationToken);
            if (_shownFor != key)
            {
                // Another profile took over meanwhile.
                return;
            }
            IsAvailable = available == true;
            if (available is not null)
            {
                _capabilityFor = key;
            }
        }
        if (!IsAvailable)
        {
            return;
        }
        int reads = ++_reads;
        try
        {
            int count = await _notifications.GetUnreadCountAsync(server.BaseUri, cancellationToken);
            if (reads == _reads && _shownFor == key)
            {
                UnreadCount = Math.Max(0, count);
                _countedAt = _time.GetUtcNow();
            }
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not read the unread notification count");
        }
    }

    /// <summary>The inbox page's own count, or one read marked.</summary>
    public void Show(int count)
    {
        ++_reads;
        UnreadCount = Math.Max(0, count);
        _countedAt = _time.GetUtcNow();
    }

    // As the web: shown while the capability loads or says yes; hidden when it says no or cannot be read (null).
    private async Task<bool?> InboxAvailableAsync(Uri server, CancellationToken cancellationToken)
    {
        try
        {
            return (await _notifications.GetCapabilitiesAsync(server, cancellationToken)).HasInbox;
        }
        catch (Exception ex) when (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning(ex, "Could not read the notification capability");
            return null;
        }
    }
}
